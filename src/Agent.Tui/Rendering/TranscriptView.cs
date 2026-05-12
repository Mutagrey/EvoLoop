using Terminal.Gui;

namespace Agent.Tui;

internal sealed class TranscriptView : View
{
    private readonly TuiTheme _theme;
    private readonly TranscriptScrollState _scroll = new();
    private IReadOnlyList<TuiMessage> _messages = Array.Empty<TuiMessage>();
    private IReadOnlyList<TranscriptRenderLine> _lines = Array.Empty<TranscriptRenderLine>();
    private int _renderWidth = -1;
    private bool _revealLatestMessageStart;
    private int? _preserveScrollLineCount;

    public TranscriptView(TuiTheme theme)
    {
        _theme = theme;
        CanFocus = false;
        ColorScheme = theme.Transcript;
    }

    public void SetMessages(IReadOnlyList<TuiMessage> messages)
    {
        var wasAtBottom = _scroll.IsAtBottom;
        var appended = _messages.Count > 0 && messages.Count > _messages.Count;
        var hadRenderedLines = _renderWidth > 0;
        _messages = messages;
        _renderWidth = -1;
        if (wasAtBottom && appended)
        {
            _revealLatestMessageStart = true;
        }
        else if (appended && hadRenderedLines)
        {
            _preserveScrollLineCount = _lines.Count;
        }
        else if (wasAtBottom)
        {
            _scroll.ScrollBottom();
        }

        SetNeedsDisplay();
    }

    public void ScrollPageUp()
    {
        EnsureRendered();
        _scroll.ScrollPageUp(_lines.Count, Math.Max(0, Bounds.Height));
        SetNeedsDisplay();
    }

    public void ScrollPageDown()
    {
        EnsureRendered();
        _scroll.ScrollPageDown(_lines.Count, Math.Max(0, Bounds.Height));
        SetNeedsDisplay();
    }

    public void ScrollTop()
    {
        EnsureRendered();
        _scroll.ScrollTop(_lines.Count, Math.Max(0, Bounds.Height));
        SetNeedsDisplay();
    }

    public void ScrollBottom()
    {
        _scroll.ScrollBottom();
        SetNeedsDisplay();
    }

    public override void Redraw(Rect bounds)
    {
        var width = Math.Max(1, Bounds.Width);
        var height = Math.Max(0, Bounds.Height);
        EnsureRendered(width);
        if (_preserveScrollLineCount is int previousLineCount)
        {
            _preserveScrollLineCount = null;
            _scroll.PreserveVisibleContentAfterAppend(previousLineCount, _lines.Count, height);
        }
        else if (_revealLatestMessageStart)
        {
            RevealLatestMessageStart(width, height);
        }

        var start = _scroll.GetStartLine(_lines.Count, height);
        for (var row = 0; row < height; row++)
        {
            var lineIndex = start + row;
            var line = lineIndex < _lines.Count ? _lines[lineIndex] : TranscriptRenderLine.Spacer;
            DrawLine(row, line, width);
        }
    }

    public override bool MouseEvent(MouseEvent mouseEvent)
    {
        if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            EnsureRendered();
            _scroll.ScrollLineUp(_lines.Count, Math.Max(0, Bounds.Height));
            SetNeedsDisplay();
            return true;
        }

        if (mouseEvent.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            EnsureRendered();
            _scroll.ScrollLineDown(_lines.Count, Math.Max(0, Bounds.Height));
            SetNeedsDisplay();
            return true;
        }

        return base.MouseEvent(mouseEvent);
    }

    private void EnsureRendered()
    {
        EnsureRendered(Math.Max(1, Bounds.Width));
    }

    private void EnsureRendered(int width)
    {
        if (_renderWidth != width)
        {
            _lines = TranscriptRenderer.RenderLines(_messages, width);
            _renderWidth = width;
        }
    }

    private void RevealLatestMessageStart(int width, int height)
    {
        _revealLatestMessageStart = false;
        if (_messages.Count == 0)
        {
            return;
        }

        var previousLines = TranscriptRenderer.RenderLines(_messages.Take(_messages.Count - 1), width).Count;
        _scroll.RevealLineAtTop(previousLines, _lines.Count, height);
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
