using Terminal.Gui;

namespace Agent.Tui;

internal sealed class TerminalGuiTuiHost
{
    public void Run(TuiApp app)
    {
        Application.Init();
        try
        {
            BuildMainWindow(app);
            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private static void BuildMainWindow(TuiApp app)
    {
        var top = Application.Top;
        var window = new Window("EvoLoop Agent")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var header = new Label(app.Header)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };

        var transcript = new TextView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            ReadOnly = true,
            WordWrap = true,
            Text = app.Transcript(SafeConsoleWidth())
        };

        var prompt = new Label(">")
        {
            X = 0,
            Y = Pos.Bottom(transcript) + 1,
            Width = 1,
            Height = 1
        };

        var input = new TextField(string.Empty)
        {
            X = 2,
            Y = Pos.Bottom(transcript) + 1,
            Width = Dim.Fill(),
            Height = 1
        };

        var status = new Label(app.StatusLine)
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1
        };

        void RefreshTranscript()
        {
            transcript.Text = app.Transcript(SafeConsoleWidth());
            status.Text = app.StatusLine;
            transcript.MoveEnd();
            transcript.SetNeedsDisplay();
            status.SetNeedsDisplay();
        }

        void Stop()
        {
            Application.RequestStop();
        }

        void SubmitInput()
        {
            var value = input.Text?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            input.Text = string.Empty;
            app.Submit(value);
            RefreshTranscript();
            if (app.ExitRequested)
            {
                Stop();
            }
        }

        input.KeyPress += args =>
        {
            var key = args.KeyEvent.Key;
            if (key == Key.Enter)
            {
                args.Handled = true;
                SubmitInput();
                return;
            }

            if (key == (Key.CtrlMask | Key.D) && string.IsNullOrWhiteSpace(input.Text?.ToString()))
            {
                args.Handled = true;
                Stop();
                return;
            }

            if (key == (Key.CtrlMask | Key.C))
            {
                args.Handled = true;
                Stop();
            }
        };

        top.KeyPress += args =>
        {
            var key = args.KeyEvent.Key;
            if (key == (Key.CtrlMask | Key.C))
            {
                args.Handled = true;
                Stop();
                return;
            }

            if (key == (Key.CtrlMask | Key.D) && string.IsNullOrWhiteSpace(input.Text?.ToString()))
            {
                args.Handled = true;
                Stop();
            }
        };

        window.Add(header, transcript, prompt, input, status);
        top.Add(window);
        input.SetFocus();
    }

    private static int SafeConsoleWidth()
    {
        try
        {
            return Math.Max(60, Console.WindowWidth - 4);
        }
        catch
        {
            return 100;
        }
    }
}
