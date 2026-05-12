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

        var choiceMenu = new ChoiceMenuView(_theme)
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
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
        ActiveChoiceMenu? activeChoiceMenu = null;

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

        bool HandleTranscriptNavigation(Key key)
        {
            if (key == Key.PageUp)
            {
                transcript.ScrollPageUp();
                return true;
            }

            if (key == Key.PageDown)
            {
                transcript.ScrollPageDown();
                return true;
            }

            if (key == Key.Home)
            {
                transcript.ScrollTop();
                return true;
            }

            if (key == Key.End)
            {
                transcript.ScrollBottom();
                return true;
            }

            return false;
        }

        void CloseChoiceMenu(string? selectedId)
        {
            var menu = activeChoiceMenu;
            if (menu is null)
            {
                return;
            }

            activeChoiceMenu = null;
            choiceMenu.ClearMenu();
            RefreshSuggestions();
            input.SetFocus();
            menu.Completion.TrySetResult(selectedId);
        }

        bool HandleActiveChoiceMenuKey(Key key)
        {
            var menu = activeChoiceMenu;
            if (menu is null)
            {
                return false;
            }

            if (key == (Key.CtrlMask | Key.C) || key == (Key.CtrlMask | Key.D))
            {
                return false;
            }

            var visible = choiceMenu.VisibleItemCount;
            if (key == Key.CursorDown)
            {
                menu.State.MoveNext(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.CursorUp)
            {
                menu.State.MovePrevious(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.PageDown)
            {
                menu.State.PageDown(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.PageUp)
            {
                menu.State.PageUp(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.Home)
            {
                menu.State.MoveHome(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.End)
            {
                menu.State.MoveEnd(visible);
                choiceMenu.RefreshMenu();
                return true;
            }

            if (key == Key.Enter)
            {
                CloseChoiceMenu(menu.State.Confirm());
                return true;
            }

            if (key == Key.Esc)
            {
                CloseChoiceMenu(ChoiceMenuState.Cancel());
                return true;
            }

            return true;
        }

        bool HandleSystemKey(Key key)
        {
            if (key == Key.Esc)
            {
                return app.CancelRunningTask();
            }

            if (key == (Key.CtrlMask | Key.C))
            {
                Stop();
                return true;
            }

            if (key == (Key.CtrlMask | Key.D) && string.IsNullOrWhiteSpace(input.Text?.ToString()))
            {
                Stop();
                return true;
            }

            return false;
        }

        bool HandleSharedKey(Key key)
        {
            return HandleActiveChoiceMenuKey(key) ||
                   HandleSystemKey(key) ||
                   HandleTranscriptNavigation(key);
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
        app.AttachChoicePrompt(ShowChoiceMenuAsync);
        app.AttachApprovalPrompt(ShowApprovalMenuAsync);

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
            if (HandleSharedKey(key))
            {
                args.Handled = true;
                return;
            }

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
            }
        };

        input.TextChanged += _ => RefreshSuggestions();

        top.KeyPress += args =>
        {
            var key = args.KeyEvent.Key;
            if (HandleSharedKey(key))
            {
                args.Handled = true;
            }
        };

        root.Add(title, profile, mode, cwdLabel, cwd, separator, transcript, suggestions, choiceMenu, prompt, input, status);
        top.Add(root);
        input.SetFocus();

        async Task<string?> ShowChoiceMenuAsync(TuiChoiceMenuRequest request, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));

            Application.MainLoop.Invoke(() =>
            {
                activeChoiceMenu?.Completion.TrySetResult(null);
                var state = new ChoiceMenuState(request.Items, request.InitialItemId);
                activeChoiceMenu = new ActiveChoiceMenu(state, tcs);
                currentSuggestions = Array.Empty<SlashCommand>();
                selectedSuggestion = 0;
                suggestions.SetSuggestions(currentSuggestions, selectedSuggestion);
                choiceMenu.SetMenu(request.Title, request.Body, state);
            });

            try
            {
                return await tcs.Task;
            }
            finally
            {
                Application.MainLoop.Invoke(() =>
                {
                    if (ReferenceEquals(activeChoiceMenu?.Completion, tcs))
                    {
                        activeChoiceMenu = null;
                        choiceMenu.ClearMenu();
                        RefreshSuggestions();
                    }
                });
            }
        }

        async Task<bool> ShowApprovalMenuAsync(ApprovalRequest request, CancellationToken ct)
        {
            var body = TuiApprovalRequestFormatter.FormatForDialog(request, 1600);
            var selected = await ShowChoiceMenuAsync(new TuiChoiceMenuRequest(
                "Approval required",
                body,
                new[]
                {
                    new ChoiceMenuItem("reject", "Reject", "Do not run this tool request."),
                    new ChoiceMenuItem("approve", "Approve", "Allow this tool request once.", IsDangerous: true)
                },
                "reject"), ct);
            RefreshTranscript();
            return string.Equals(selected, "approve", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed record ActiveChoiceMenu(
        ChoiceMenuState State,
        TaskCompletionSource<string?> Completion);

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
