using Agent.Core;
using Terminal.Gui;

namespace Agent.Tui;

internal sealed class TerminalGuiTuiHost
{
    private readonly TuiTheme _theme;

    public TerminalGuiTuiHost(TuiTheme theme)
    {
        _theme = theme;
    }

    public void Run(TuiApp app)
    {
        Application.Init();
        try
        {
            _theme.ApplyGlobals();
            BuildMainWindow(app);
            Application.Run();
        }
        finally
        {
            Application.Shutdown();
        }
    }

    private void BuildMainWindow(TuiApp app)
    {
        var top = Application.Top;
        top.ColorScheme = _theme.TopLevel;

        var root = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = _theme.TopLevel
        };

        var title = new Label("EvoLoop")
        {
            X = 0,
            Y = 0,
            Width = 8,
            Height = 1,
            ColorScheme = _theme.Title
        };

        var profile = new Label($"profile {app.Runtime.Profile}")
        {
            X = Pos.Right(title) + 2,
            Y = 0,
            Width = 22,
            Height = 1,
            ColorScheme = _theme.Muted
        };

        var mode = new Label($"mode {app.Runtime.ModeLabel}")
        {
            X = Pos.Right(profile) + 2,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Muted
        };

        var cwdLabel = new Label("cwd")
        {
            X = 0,
            Y = 1,
            Width = 3,
            Height = 1,
            ColorScheme = _theme.Muted
        };

        var cwd = new Label(app.Runtime.Workspace)
        {
            X = 5,
            Y = 1,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Path
        };

        var separator = new Label(new string('-', 120))
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Muted
        };

        var transcript = new TextView
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            ReadOnly = true,
            WordWrap = true,
            Text = app.Transcript(SafeConsoleWidth()),
            ColorScheme = _theme.Transcript
        };

        var prompt = new Label(">")
        {
            X = 0,
            Y = Pos.Bottom(transcript) + 1,
            Width = 1,
            Height = 1,
            ColorScheme = _theme.Prompt
        };

        var input = new TextField(string.Empty)
        {
            X = 2,
            Y = Pos.Bottom(transcript) + 1,
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Input
        };

        var status = new Label(app.StatusLine)
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Status
        };

        void RefreshTranscript()
        {
            transcript.Text = app.Transcript(SafeConsoleWidth());
            status.Text = app.StatusLine;
            transcript.MoveEnd();
            transcript.SetNeedsDisplay();
            status.SetNeedsDisplay();
        }

        app.Changed += () => Application.MainLoop.Invoke(RefreshTranscript);
        app.AttachApprovalPrompt(ShowApprovalDialogAsync);

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
            RefreshTranscript();
            _ = Task.Run(async () =>
            {
                await app.SubmitAsync(value, CancellationToken.None);
                if (app.ExitRequested)
                {
                    Application.MainLoop.Invoke(Stop);
                }
            });
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

        root.Add(title, profile, mode, cwdLabel, cwd, separator, transcript, prompt, input, status);
        top.Add(root);
        input.SetFocus();

        async Task<bool> ShowApprovalDialogAsync(ApprovalRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));

            Application.MainLoop.Invoke(() =>
            {
                var message =
                    $"Tool: {request.ToolName}\n" +
                    $"Reason: {request.Reason}\n\n" +
                    $"Arguments:\n{Clip(request.ArgumentsPreview, 1600)}";
                var result = MessageBox.Query("Approval Required", message, "Approve", "Reject");
                tcs.TrySetResult(result == 0);
                RefreshTranscript();
            });

            return await tcs.Task;
        }
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

    private static string Clip(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
