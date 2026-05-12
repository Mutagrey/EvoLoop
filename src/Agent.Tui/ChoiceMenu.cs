namespace Agent.Tui;

internal sealed record ChoiceMenuItem(
    string Id,
    string Title,
    string Description,
    bool IsCurrent = false,
    bool IsDisabled = false,
    bool IsDangerous = false);

internal sealed record TuiChoiceMenuRequest(
    string Title,
    string Body,
    IReadOnlyList<ChoiceMenuItem> Items,
    string? InitialItemId = null);

internal sealed class ChoiceMenuState
{
    private readonly IReadOnlyList<ChoiceMenuItem> _items;

    public ChoiceMenuState(IReadOnlyList<ChoiceMenuItem> items, string? initialItemId = null)
    {
        _items = items;
        SelectedIndex = ResolveInitialIndex(initialItemId);
    }

    public IReadOnlyList<ChoiceMenuItem> Items => _items;
    public int SelectedIndex { get; private set; }
    public int TopIndex { get; private set; }
    public ChoiceMenuItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < _items.Count ? _items[SelectedIndex] : null;

    public string? Confirm()
    {
        var item = SelectedItem;
        return item is null || item.IsDisabled ? null : item.Id;
    }

    public static string? Cancel() => null;

    public void MoveNext(int visibleItemCount)
    {
        Move(1, visibleItemCount);
    }

    public void MovePrevious(int visibleItemCount)
    {
        Move(-1, visibleItemCount);
    }

    public void PageDown(int visibleItemCount)
    {
        Move(Math.Max(1, visibleItemCount), visibleItemCount);
    }

    public void PageUp(int visibleItemCount)
    {
        Move(-Math.Max(1, visibleItemCount), visibleItemCount);
    }

    public void MoveHome(int visibleItemCount)
    {
        SelectedIndex = FirstEnabledIndex();
        EnsureVisible(visibleItemCount);
    }

    public void MoveEnd(int visibleItemCount)
    {
        SelectedIndex = LastEnabledIndex();
        EnsureVisible(visibleItemCount);
    }

    public void EnsureVisible(int visibleItemCount)
    {
        if (_items.Count == 0)
        {
            TopIndex = 0;
            return;
        }

        var visible = Math.Max(1, visibleItemCount);
        if (SelectedIndex < TopIndex)
        {
            TopIndex = SelectedIndex;
        }
        else if (SelectedIndex >= TopIndex + visible)
        {
            TopIndex = SelectedIndex - visible + 1;
        }

        TopIndex = Math.Clamp(TopIndex, 0, Math.Max(0, _items.Count - visible));
    }

    private int ResolveInitialIndex(string? initialItemId)
    {
        if (!string.IsNullOrWhiteSpace(initialItemId))
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (!_items[i].IsDisabled &&
                    _items[i].Id.Equals(initialItemId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].IsCurrent && !_items[i].IsDisabled)
            {
                return i;
            }
        }

        return FirstEnabledIndex();
    }

    private void Move(int delta, int visibleItemCount)
    {
        if (_items.Count == 0)
        {
            SelectedIndex = -1;
            TopIndex = 0;
            return;
        }

        var index = SelectedIndex;
        for (var step = 0; step < _items.Count; step++)
        {
            index = (index + delta + _items.Count) % _items.Count;
            if (!_items[index].IsDisabled)
            {
                SelectedIndex = index;
                EnsureVisible(visibleItemCount);
                return;
            }
        }
    }

    private int FirstEnabledIndex()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (!_items[i].IsDisabled)
            {
                return i;
            }
        }

        return _items.Count == 0 ? -1 : 0;
    }

    private int LastEnabledIndex()
    {
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!_items[i].IsDisabled)
            {
                return i;
            }
        }

        return _items.Count == 0 ? -1 : 0;
    }
}
