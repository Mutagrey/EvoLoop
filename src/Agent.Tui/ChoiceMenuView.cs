using Terminal.Gui;

namespace Agent.Tui;

internal sealed class ChoiceMenuView : View
{
    private readonly TuiTheme _theme;
    private string _title = string.Empty;
    private IReadOnlyList<string> _bodyLines = Array.Empty<string>();
    private ChoiceMenuState? _state;

    public ChoiceMenuView(TuiTheme theme)
    {
        _theme = theme;
        CanFocus = false;
        Visible = false;
        ColorScheme = theme.Chrome;
    }

    public int VisibleItemCount
    {
        get
        {
            var height = Math.Max(0, Bounds.Height);
            var bodyRows = GetBodyRowCount(height);
            var available = Math.Max(0, height - bodyRows - 3);
            return Math.Max(1, available / 2);
        }
    }

    public void SetMenu(string title, string body, ChoiceMenuState state)
    {
        _title = title;
        _bodyLines = SplitBody(body);
        _state = state;
        Visible = true;
        state.EnsureVisible(VisibleItemCount);
        SetNeedsDisplay();
    }

    public void ClearMenu()
    {
        _state = null;
        _title = string.Empty;
        _bodyLines = Array.Empty<string>();
        Visible = false;
        SetNeedsDisplay();
    }

    public void RefreshMenu()
    {
        _state?.EnsureVisible(VisibleItemCount);
        SetNeedsDisplay();
    }

    public override void Redraw(Rect bounds)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);
        Fill(width, height);

        if (_state is null)
        {
            return;
        }

        DrawText(0, _title, _theme.Title.Normal, width);
        var row = 1;
        var bodyRows = GetBodyRowCount(height);
        for (var i = 0; i < bodyRows; i++)
        {
            var text = i < _bodyLines.Count ? _bodyLines[i] : string.Empty;
            if (i == bodyRows - 1 && _bodyLines.Count > bodyRows)
            {
                text = "...";
            }

            DrawText(row++, text, _theme.Transcript.Normal, width);
        }

        DrawText(row++, new string('-', Math.Min(width, 120)), _theme.Muted.Normal, width);
        var visibleItems = VisibleItemCount;
        _state.EnsureVisible(visibleItems);

        for (var i = 0; i < visibleItems; i++)
        {
            var itemIndex = _state.TopIndex + i;
            if (itemIndex >= _state.Items.Count)
            {
                break;
            }

            var item = _state.Items[itemIndex];
            var selected = itemIndex == _state.SelectedIndex;
            var attribute = ResolveAttribute(item, selected);
            var marker = selected ? "* " : "  ";
            var current = item.IsCurrent ? " (current)" : string.Empty;
            var disabled = item.IsDisabled ? " (unavailable)" : string.Empty;

            DrawText(row++, $"{marker}{item.Title}{current}{disabled}", attribute, width);
            DrawText(row++, $"  {item.Description}", _theme.Muted.Normal, width);
        }

        var footer = "Enter select  Esc cancel  Up/Down move  PgUp/PgDn scroll";
        DrawText(height - 1, footer, _theme.Muted.Normal, width);
    }

    private Terminal.Gui.Attribute ResolveAttribute(ChoiceMenuItem item, bool selected)
    {
        if (item.IsDisabled)
        {
            return _theme.Muted.Normal;
        }

        if (item.IsDangerous)
        {
            return selected ? _theme.Error.Focus : _theme.Error.Normal;
        }

        return selected ? _theme.Prompt.Focus : _theme.Transcript.Normal;
    }

    private void Fill(int width, int height)
    {
        Driver.SetAttribute(_theme.Chrome.Normal);
        for (var row = 0; row < height; row++)
        {
            Move(0, row);
            Driver.AddStr(new string(' ', width));
        }
    }

    private int GetBodyRowCount(int height)
    {
        if (_bodyLines.Count == 0)
        {
            return 0;
        }

        return Math.Min(_bodyLines.Count, Math.Max(1, Math.Min(8, height / 2)));
    }

    private void DrawText(int row, string text, Terminal.Gui.Attribute attribute, int width)
    {
        if (row < 0 || row >= Bounds.Height)
        {
            return;
        }

        Move(0, row);
        Driver.SetAttribute(attribute);
        Driver.AddStr(Fit(text, width));
    }

    private static IReadOnlyList<string> SplitBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return Array.Empty<string>();
        }

        return body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    private static string Fit(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var trimmed = text.Length <= width ? text : text[..width];
        return trimmed.PadRight(width);
    }
}
