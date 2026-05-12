namespace Agent.Tui;

internal sealed class TranscriptScrollState
{
    public int OffsetFromBottom { get; private set; }
    public bool IsAtBottom => OffsetFromBottom == 0;

    public int GetStartLine(int lineCount, int viewportHeight)
    {
        Clamp(lineCount, viewportHeight);
        return Math.Max(0, lineCount - Math.Max(0, viewportHeight) - OffsetFromBottom);
    }

    public void ScrollPageUp(int lineCount, int viewportHeight)
    {
        OffsetFromBottom += Math.Max(1, viewportHeight - 1);
        Clamp(lineCount, viewportHeight);
    }

    public void ScrollPageDown(int lineCount, int viewportHeight)
    {
        OffsetFromBottom -= Math.Max(1, viewportHeight - 1);
        Clamp(lineCount, viewportHeight);
    }

    public void ScrollLineUp(int lineCount, int viewportHeight)
    {
        OffsetFromBottom++;
        Clamp(lineCount, viewportHeight);
    }

    public void ScrollLineDown(int lineCount, int viewportHeight)
    {
        OffsetFromBottom--;
        Clamp(lineCount, viewportHeight);
    }

    public void ScrollTop(int lineCount, int viewportHeight)
    {
        OffsetFromBottom = MaxOffset(lineCount, viewportHeight);
    }

    public void RevealLineAtTop(int lineIndex, int lineCount, int viewportHeight)
    {
        var max = MaxOffset(lineCount, viewportHeight);
        OffsetFromBottom = Math.Clamp(lineCount - Math.Max(0, viewportHeight) - Math.Max(0, lineIndex), 0, max);
    }

    public void PreserveVisibleContentAfterAppend(int previousLineCount, int lineCount, int viewportHeight)
    {
        OffsetFromBottom += Math.Max(0, lineCount - previousLineCount);
        Clamp(lineCount, viewportHeight);
    }

    public void ScrollBottom()
    {
        OffsetFromBottom = 0;
    }

    public void Clamp(int lineCount, int viewportHeight)
    {
        OffsetFromBottom = Math.Clamp(OffsetFromBottom, 0, MaxOffset(lineCount, viewportHeight));
    }

    private static int MaxOffset(int lineCount, int viewportHeight)
    {
        return Math.Max(0, lineCount - Math.Max(0, viewportHeight));
    }
}
