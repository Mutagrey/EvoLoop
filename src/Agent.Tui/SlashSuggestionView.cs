using Terminal.Gui;

namespace Agent.Tui;

internal sealed class SlashSuggestionView : View
{
    private readonly TuiTheme _theme;
    private IReadOnlyList<SlashCommand> _suggestions = Array.Empty<SlashCommand>();
    private int _selectedIndex;

    public SlashSuggestionView(TuiTheme theme)
    {
        _theme = theme;
        CanFocus = false;
        Visible = false;
        ColorScheme = theme.Chrome;
    }

    public void SetSuggestions(IReadOnlyList<SlashCommand> suggestions, int selectedIndex)
    {
        _suggestions = suggestions;
        _selectedIndex = suggestions.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, suggestions.Count - 1);
        Visible = suggestions.Count > 0;
        SetNeedsDisplay();
    }

    public override void Redraw(Rect bounds)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);

        for (var row = 0; row < height; row++)
        {
            Move(0, row);
            Driver.SetAttribute(_theme.Chrome.Normal);
            Driver.AddStr(new string(' ', width));
        }

        if (_suggestions.Count == 0)
        {
            return;
        }

        DrawText(0, "slash commands", _theme.Muted.Normal, width);
        var visibleRows = Math.Min(_suggestions.Count, Math.Max(0, height - 1));
        for (var i = 0; i < visibleRows; i++)
        {
            var command = _suggestions[i];
            var selected = i == _selectedIndex;
            var marker = selected ? "> " : "  ";
            var text = $"{marker}{command.Usage,-26} {command.Description}";
            DrawText(i + 1, text, selected ? _theme.Prompt.Normal : _theme.Transcript.Normal, width);
        }
    }

    private void DrawText(int row, string text, Terminal.Gui.Attribute attribute, int width)
    {
        Move(0, row);
        Driver.SetAttribute(attribute);
        Driver.AddStr(Fit(text, width));
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
