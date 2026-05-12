using Terminal.Gui;

namespace Agent.Tui;

internal sealed class TranscriptView : View
{
    private readonly TuiTheme _theme;
    private IReadOnlyList<TuiMessage> _messages = Array.Empty<TuiMessage>();
    private IReadOnlyList<TranscriptRenderLine> _lines = Array.Empty<TranscriptRenderLine>();
    private int _renderWidth = -1;

    public TranscriptView(TuiTheme theme)
    {
        _theme = theme;
        CanFocus = false;
        ColorScheme = theme.Transcript;
    }

    public void SetMessages(IReadOnlyList<TuiMessage> messages)
    {
        _messages = messages;
        _renderWidth = -1;
        SetNeedsDisplay();
    }

    public override void Redraw(Rect bounds)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);
        if (_renderWidth != width)
        {
            _lines = TranscriptRenderer.RenderLines(_messages, width);
            _renderWidth = width;
        }

        var start = Math.Max(0, _lines.Count - height);
        for (var row = 0; row < height; row++)
        {
            var lineIndex = start + row;
            var line = lineIndex < _lines.Count ? _lines[lineIndex] : TranscriptRenderLine.Spacer;
            DrawLine(row, line, width);
        }
    }

    private void DrawLine(int row, TranscriptRenderLine line, int width)
    {
        Move(0, row);
        Driver.SetAttribute(GetAttribute(line));
        Driver.AddStr(Fit(line.Text, width));
    }

    private Terminal.Gui.Attribute GetAttribute(TranscriptRenderLine line)
    {
        if (line.Role == TuiMessageRole.Error)
        {
            return _theme.Error.Normal;
        }

        if (line.IsImportant)
        {
            return _theme.Important.Normal;
        }

        if (line.Text.Length == 0)
        {
            return _theme.Transcript.Normal;
        }

        return line.Role switch
        {
            TuiMessageRole.User => _theme.User.Normal,
            TuiMessageRole.Assistant => _theme.Assistant.Normal,
            TuiMessageRole.System => line.IsHeader ? _theme.Muted.Normal : _theme.Transcript.Normal,
            _ => _theme.Status.Normal
        };
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
