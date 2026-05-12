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

        var title = new Label("EvoLoop Agent")
        {
            X = 0,
            Y = 0,
            Width = 13,
            Height = 1,
            ColorScheme = _theme.Title
        };

        var profile = new Label($"model profile {app.Runtime.Profile}")
        {
            X = Pos.Right(title) + 2,
            Y = 0,
            Width = 32,
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

        var transcript = new TranscriptView(_theme)
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
            ColorScheme = _theme.Transcript
        };
        transcript.SetMessages(app.Messages);

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

        var suggestions = new SlashSuggestionView(_theme)
        {
            X = 0,
            Y = Pos.Bottom(transcript) - 6,
            Width = Dim.Fill(),
            Height = 6,
            ColorScheme = _theme.Chrome
        };

        var status = new Label(BuildStatusLine(app, 0))
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            ColorScheme = _theme.Status
        };

        var spinnerFrame = 0;
        var selectedSuggestion = 0;
        IReadOnlyList<SlashCommand> currentSuggestions = Array.Empty<SlashCommand>();

        void RefreshTranscript()
        {
            transcript.SetMessages(app.Messages);
            profile.Text = $"model profile {app.Runtime.Profile}";
            mode.Text = $"mode {app.Runtime.ModeLabel}";
            cwd.Text = app.Runtime.Workspace;
            status.Text = BuildStatusLine(app, spinnerFrame);
            status.ColorScheme = app.IsModelThinking ? _theme.Thinking : _theme.Status;
            transcript.SetNeedsDisplay();
            profile.SetNeedsDisplay();
            mode.SetNeedsDisplay();
            cwd.SetNeedsDisplay();
            status.SetNeedsDisplay();
        }

        void RefreshSuggestions()
        {
            var value = input.Text?.ToString() ?? string.Empty;
            var prefix = ExtractSlashPrefix(value);
            if (prefix is null)
            {
                currentSuggestions = Array.Empty<SlashCommand>();
                selectedSuggestion = 0;
                suggestions.SetSuggestions(currentSuggestions, selectedSuggestion);
                return;
            }

            currentSuggestions = app.Commands.Filter(prefix).Take(5).ToArray();
            if (selectedSuggestion >= currentSuggestions.Count)
            {
                selectedSuggestion = Math.Max(0, currentSuggestions.Count - 1);
            }

            suggestions.SetSuggestions(currentSuggestions, selectedSuggestion);
        }

        void CompleteSelectedSuggestion()
        {
            if (currentSuggestions.Count == 0)
            {
                return;
            }

            var command = currentSuggestions[selectedSuggestion].Name;
            input.Text = command + " ";
            input.CursorPosition = input.Text.RuneCount;
            RefreshSuggestions();
            input.SetNeedsDisplay();
        }

        Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(180), _ =>
        {
            if (app.IsTaskRunning)
            {
                spinnerFrame++;
                status.Text = BuildStatusLine(app, spinnerFrame);
                status.ColorScheme = app.IsModelThinking ? _theme.Thinking : _theme.Status;
                status.SetNeedsDisplay();
            }

            return true;
        });

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
            RefreshSuggestions();
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
            if (key == Key.Tab && currentSuggestions.Count > 0)
            {
                args.Handled = true;
                CompleteSelectedSuggestion();
                return;
            }

            if (key == Key.CursorDown && currentSuggestions.Count > 0)
            {
                args.Handled = true;
                selectedSuggestion = (selectedSuggestion + 1) % currentSuggestions.Count;
                suggestions.SetSuggestions(currentSuggestions, selectedSuggestion);
                return;
            }

            if (key == Key.CursorUp && currentSuggestions.Count > 0)
            {
                args.Handled = true;
                selectedSuggestion = (selectedSuggestion - 1 + currentSuggestions.Count) % currentSuggestions.Count;
                suggestions.SetSuggestions(currentSuggestions, selectedSuggestion);
                return;
            }

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

        input.TextChanged += _ => RefreshSuggestions();

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

        root.Add(title, profile, mode, cwdLabel, cwd, separator, transcript, suggestions, prompt, input, status);
        top.Add(root);
        input.SetFocus();

        async Task<bool> ShowApprovalDialogAsync(ApprovalRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));

            Application.MainLoop.Invoke(() =>
            {
                var message = TuiApprovalRequestFormatter.FormatForDialog(request, 1600);
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

    private static string BuildStatusLine(TuiApp app, int spinnerFrame)
    {
        if (!app.IsTaskRunning)
        {
            return app.StatusLine;
        }

        var frame = (spinnerFrame % 4) switch
        {
            0 => "|",
            1 => "/",
            2 => "-",
            _ => "\\"
        };
        var label = app.IsModelThinking ? "thinking" : "working";
        return $"{label} {frame} | {app.StatusLine}";
    }

    private static string? ExtractSlashPrefix(string value)
    {
        var text = value.TrimStart();
        if (!text.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? "/" : first;
    }

}
