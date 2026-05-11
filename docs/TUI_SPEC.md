# TUI Interface Blueprint for Local Coding Agent

## Context

We are building a local-first C#/.NET coding agent inspired by Claude Code-style terminal UX.

The project should work on a restricted corporate Windows machine:
- No admin rights.
- Limited internet access.
- Prefer built-in .NET APIs and vendored dependencies.
- Do not introduce Node.js, npm, React, Vite, Python, Docker, or cloud-only dependencies.
- The app should run as a CLI/TUI executable.
- The agent talks to an OpenAI-compatible corporate API through a local proxy.
- The system should remain local-first: local JSON storage, local workspace, local logs.

The goal is not to clone Claude Code visually one-to-one, but to create a professional terminal interface with similar usability:
- convenient chat flow;
- visible agent progress;
- slash commands;
- command suggestions;
- readable tool calls;
- approvals;
- diffs;
- session history;
- clear status/errors;
- useful message rendering.

---

# Main Goal

Design and implement a production-grade TUI layer for the local coding agent.

The TUI should become the main interactive interface for using the agent from terminal.

It must support:

1. Chat input with multiline editing.
2. Slash-command menu when the user types `/`.
3. Streaming assistant output.
4. Visible agent steps: thinking/status, tool calls, file reads, patches, approvals.
5. Safe workspace operations.
6. Useful rendering of messages, code blocks, diffs, errors, and tool results.
7. Session tree / history navigation.
8. Keyboard-first UX.
9. Clean architecture, separated from agent runtime.
10. Testable components.

---

# Important Constraint

Do not rewrite the whole project.

First inspect the existing repository structure and identify:
- current CLI entry point;
- current agent runtime;
- current tool registry;
- current session storage;
- current message model;
- current streaming support;
- current approval flow, if any;
- current logging system.

Then create a focused implementation plan.

If something is missing, introduce minimal clean abstractions instead of large rewrites.

---

# Desired User Experience

The TUI should feel like a professional coding assistant running inside terminal.

## Layout

Use a simple, robust layout:

```text
┌──────────────────────────────────────────────────────────────┐
│ EvoLoop Agent                         model: qwen / glm / api │
├──────────────────────────────────────────────────────────────┤
│ Conversation                                                 │
│                                                              │
│ User: fix broken tower defense game                          │
│                                                              │
│ Assistant: I will inspect the project structure first.        │
│                                                              │
│ ▸ tool: list_files ./                                        │
│   result: 42 files                                           │
│                                                              │
│ ▸ tool: read_file Game.cs                                    │
│   result: found broken enemy pathing                         │
│                                                              │
│ ▸ patch: Game.cs                                             │
│   + fixed path update loop                                   │
│   + added null guard                                         │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│ Status: waiting for input | tokens | cwd | session name       │
├──────────────────────────────────────────────────────────────┤
│ > /help                                                      │
└──────────────────────────────────────────────────────────────┘