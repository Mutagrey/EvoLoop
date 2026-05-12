using Terminal.Gui;

namespace Agent.Tui;

internal sealed class TuiTheme
{
    public const string DefaultName = "claude-dark";
    public const string NoColorName = "mono";

    private TuiTheme(
        string name,
        SchemeColors topLevel,
        SchemeColors chrome,
        SchemeColors title,
        SchemeColors muted,
        SchemeColors path,
        SchemeColors transcript,
        SchemeColors input,
        SchemeColors prompt,
        SchemeColors status,
        SchemeColors error)
    {
        Name = name;
        TopLevelColors = topLevel;
        ChromeColors = chrome;
        TitleColors = title;
        MutedColors = muted;
        PathColors = path;
        TranscriptColors = transcript;
        InputColors = input;
        PromptColors = prompt;
        StatusColors = status;
        ErrorColors = error;
    }

    public string Name { get; }
    private SchemeColors TopLevelColors { get; }
    private SchemeColors ChromeColors { get; }
    private SchemeColors TitleColors { get; }
    private SchemeColors MutedColors { get; }
    private SchemeColors PathColors { get; }
    private SchemeColors TranscriptColors { get; }
    private SchemeColors InputColors { get; }
    private SchemeColors PromptColors { get; }
    private SchemeColors StatusColors { get; }
    private SchemeColors ErrorColors { get; }

    public ColorScheme TopLevel => Scheme(TopLevelColors);
    public ColorScheme Chrome => Scheme(ChromeColors);
    public ColorScheme Title => Scheme(TitleColors);
    public ColorScheme Muted => Scheme(MutedColors);
    public ColorScheme Path => Scheme(PathColors);
    public ColorScheme Transcript => Scheme(TranscriptColors);
    public ColorScheme Input => Scheme(InputColors);
    public ColorScheme Prompt => Scheme(PromptColors);
    public ColorScheme Status => Scheme(StatusColors);
    public ColorScheme Error => Scheme(ErrorColors);

    public static TuiTheme Resolve(string? name, bool noColor)
    {
        if (noColor)
        {
            return CreateMono();
        }

        return string.Equals(name, NoColorName, StringComparison.OrdinalIgnoreCase)
            ? CreateMono()
            : CreateClaudeDark();
    }

    public void ApplyGlobals()
    {
        Colors.TopLevel = TopLevel;
        Colors.Base = Transcript;
        Colors.Dialog = Chrome;
        Colors.Menu = Chrome;
        Colors.Error = Error;
    }

    private static TuiTheme CreateClaudeDark()
    {
        const Color bg = Color.Black;
        const Color fg = Color.Gray;
        const Color muted = Color.DarkGray;
        const Color bright = Color.White;
        const Color amber = Color.BrightYellow;
        const Color amberDim = Color.Brown;

        return new TuiTheme(
            DefaultName,
            new SchemeColors(fg, bg, bright, bg, amber, bg),
            new SchemeColors(fg, bg, bright, bg, amber, bg),
            new SchemeColors(bright, bg, amber, bg, amber, bg),
            new SchemeColors(muted, bg, fg, bg, amberDim, bg),
            new SchemeColors(amber, bg, bright, bg, amber, bg),
            new SchemeColors(fg, bg, bright, bg, amber, bg),
            new SchemeColors(fg, bg, bright, bg, amber, bg),
            new SchemeColors(amber, bg, bright, bg, amber, bg),
            new SchemeColors(muted, bg, fg, bg, amberDim, bg),
            new SchemeColors(Color.BrightRed, bg, bright, bg, Color.BrightRed, bg));
    }

    private static TuiTheme CreateMono()
    {
        const Color bg = Color.Black;
        const Color fg = Color.Gray;
        const Color bright = Color.White;
        const Color muted = Color.DarkGray;

        return new TuiTheme(
            NoColorName,
            new SchemeColors(fg, bg, bright, bg, fg, bg),
            new SchemeColors(fg, bg, bright, bg, fg, bg),
            new SchemeColors(bright, bg, bright, bg, bright, bg),
            new SchemeColors(muted, bg, fg, bg, muted, bg),
            new SchemeColors(bright, bg, bright, bg, bright, bg),
            new SchemeColors(fg, bg, bright, bg, fg, bg),
            new SchemeColors(fg, bg, bright, bg, fg, bg),
            new SchemeColors(bright, bg, bright, bg, bright, bg),
            new SchemeColors(muted, bg, fg, bg, muted, bg),
            new SchemeColors(bright, bg, bright, bg, bright, bg));
    }

    private static ColorScheme Scheme(SchemeColors colors)
    {
        return Scheme(
            colors.NormalForeground,
            colors.NormalBackground,
            colors.FocusForeground,
            colors.FocusBackground,
            colors.HotForeground,
            colors.HotBackground);
    }

    private static ColorScheme Scheme(
        Color normalFg,
        Color normalBg,
        Color focusFg,
        Color focusBg,
        Color hotFg,
        Color hotBg)
    {
        return new ColorScheme
        {
            Normal = Terminal.Gui.Attribute.Make(normalFg, normalBg),
            Focus = Terminal.Gui.Attribute.Make(focusFg, focusBg),
            HotNormal = Terminal.Gui.Attribute.Make(hotFg, hotBg),
            HotFocus = Terminal.Gui.Attribute.Make(hotFg, focusBg),
            Disabled = Terminal.Gui.Attribute.Make(Color.DarkGray, normalBg)
        };
    }

    private sealed record SchemeColors(
        Color NormalForeground,
        Color NormalBackground,
        Color FocusForeground,
        Color FocusBackground,
        Color HotForeground,
        Color HotBackground);
}
