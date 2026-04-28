# ARCHITECTURE.md — Offline-first C# Agent CLI

> Цель документа: выровнять существующий C# CLI-agent проект под архитектуру современного coding-agent CLI уровня Claude Code / Codex CLI, но с жёстким ограничением: на целевой машине нет интернета, кроме доступа к LLM API. Нельзя рассчитывать на загрузку NuGet-пакетов, npm, pip, внешние MCP-сервера или онлайн-документацию во время работы.

---

## 1. Контекст и главная цель

Проект уже существует, но работает нестабильно/криво. Нужно не переписать всё хаотично, а привести его к понятной архитектуре:

- CLI должен быть удобным интерактивным агентом для работы с локальным проектом.
- Агент должен уметь читать файлы, искать по проекту, предлагать план, вносить изменения, запускать локальные команды и проверки.
- Все действия с файловой системой и командами должны быть контролируемыми.
- Должен быть режим без интернета: никакие зависимости не скачиваются на целевой машине.
- Единственный разрешённый внешний доступ — LLM API endpoint.
- Проект должен быть переносимым: один собранный бинарник или папка publish-output.
- Архитектура должна позволять добавлять tools, память, режимы, UI-рендеринг и политики безопасности без переписывания ядра.

---

## 2. Принципы архитектуры

### 2.1. Agent Harness вместо “умного чата”

CLI-agent должен состоять из agent harness — управляющей оболочки, которая:

1. принимает user input;
2. собирает контекст;
3. отправляет запрос в LLM;
4. принимает tool calls / structured actions;
5. валидирует действия через policy layer;
6. выполняет действия локально;
7. возвращает observation модели;
8. повторяет цикл до финального ответа.

Модель не должна напрямую выполнять команды или писать файлы. Она только предлагает действия. Harness принимает решение, можно ли это действие выполнить.

---

### 2.2. Offline-first

Запрещено проектировать функциональность, которая требует:

- загрузки NuGet-пакетов на целевой машине;
- установки Node/Python tooling;
- внешних search API;
- внешних MCP-сервисов;
- онлайн-индексации;
- remote shell;
- cloud sandbox.

Разрешено:

- HTTP(S) запросы к LLM API;
- работа с локальной файловой системой;
- запуск локально уже установленных команд, если они есть;
- работа с локальными индексами/памятью;
- использование только тех зависимостей, которые уже встроены в publish-output.

---

### 2.3. Минимум внешних зависимостей

Базовый вариант должен быть на стандартной библиотеке .NET:

- `System.Console`
- `System.CommandLine` не использовать, если пакет не vendored/local
- `System.Text.Json`
- `System.Net.Http`
- `System.Diagnostics.Process`
- `System.IO`
- `System.Threading.Channels`
- `System.Text.RegularExpressions`

Если сейчас в проекте есть внешние зависимости, Codex должен:

1. составить список зависимостей;
2. определить, какие критичны;
3. заменить несущественные на стандартную библиотеку;
4. оставить только те, которые уже включены в репозиторий или publish artifact;
5. проверить, что `dotnet publish` не требует интернета.

---

## 3. Целевая структура проекта

Рекомендуемая структура:

```text
/src
  /AgentCli
    Program.cs
    AppBootstrapper.cs

  /AgentCli.Core
    /AgentLoop
      AgentSession.cs
      AgentRunner.cs
      AgentTurn.cs
      AgentState.cs
      AgentEvent.cs
      AgentDecision.cs

    /Planning
      Plan.cs
      PlanStep.cs
      PlanTracker.cs
      PlanRenderer.cs

    /Context
      ContextBuilder.cs
      ContextBudget.cs
      ContextItem.cs
      WorkspaceSnapshot.cs
      FileContextProvider.cs
      MemoryContextProvider.cs

    /Tools
      ITool.cs
      ToolCall.cs
      ToolResult.cs
      ToolRegistry.cs
      ToolExecutor.cs
      ToolSchema.cs

    /Policies
      ApprovalPolicy.cs
      SandboxPolicy.cs
      FileAccessPolicy.cs
      CommandPolicy.cs
      RiskLevel.cs

    /Memory
      MemoryStore.cs
      MemoryIndex.cs
      MemoryIngestor.cs
      MemoryCompactor.cs
      MemoryRecord.cs

    /Workspace
      Workspace.cs
      WorkspacePath.cs
      FileChange.cs
      PatchApplier.cs
      DiffBuilder.cs

    /LLM
      ILlmClient.cs
      LlmRequest.cs
      LlmResponse.cs
      LlmMessage.cs
      LlmToolCall.cs
      LlmStreamEvent.cs

    /Logging
      EventLog.cs
      JsonlLogger.cs
      SessionRecorder.cs

  /AgentCli.Infrastructure
    /LLM
      OpenAiCompatibleClient.cs
      StreamingParser.cs

    /Shell
      ProcessRunner.cs
      CommandResult.cs
      CommandTimeout.cs

    /FileSystem
      LocalFileSystem.cs
      SafePathResolver.cs

    /Persistence
      JsonFileStore.cs
      JsonlStore.cs

  /AgentCli.Tui
    ConsoleRenderer.cs
    InputReader.cs
    StatusBar.cs
    MarkdownLiteRenderer.cs
    DiffRenderer.cs
    Spinner.cs
    Theme.cs

  /AgentCli.Tests
    ...
/docs
  ARCHITECTURE.md
  AGENT_POLICY.md
  TOOLS.md
  MEMORY.md
  PROMPTS.md
  MIGRATION_CHECKLIST.md
/agent
  AGENTS.md
  skills/
    refactor.md
    review.md
    test.md
    offline.md
```

Если проект маленький, можно оставить меньше проектов, но слои должны быть логически разделены.

---

## 4. Главные слои

### 4.1. CLI Layer

Отвечает только за:

- чтение пользовательского ввода;
- вывод в консоль;
- выбор режима запуска;
- передачу команд в Core;
- отображение событий agent loop.

CLI не должен:

- напрямую вызывать LLM;
- напрямую писать файлы;
- напрямую выполнять shell commands;
- содержать бизнес-логику агента.

---

### 4.2. Core Layer

Содержит чистую логику:

- agent loop;
- планирование;
- tool registry;
- политики;
- контекст;
- память;
- модель событий;
- orchestration.

Core не должен зависеть от конкретного HTTP-клиента, файловой системы или консоли.

---

### 4.3. Infrastructure Layer

Содержит реализации:

- LLM API client;
- локальная файловая система;
- shell runner;
- JSON/JSONL persistence;
- process execution;
- streaming parser.

Infrastructure можно заменять без изменения Core.

---

### 4.4. TUI Layer

Содержит консольный интерфейс:

- красивый вывод;
- markdown-lite rendering;
- diff rendering;
- status bar;
- progress;
- streaming response;
- prompt input;
- keyboard shortcuts.

Важно: TUI не должен ломать агентную логику. Если TUI сломался, должен оставаться простой fallback renderer.

---

## 5. Agent Loop

Целевой цикл:

```text
User Input
  ↓
Intent Classification
  ↓
Context Build
  ↓
LLM Request
  ↓
Model Response
  ↓
Parse:
    - final answer
    - tool call
    - plan update
    - clarification
  ↓
Policy Check
  ↓
Optional Human Approval
  ↓
Tool Execution
  ↓
Observation
  ↓
Memory/Event Log Update
  ↓
Next LLM Turn or Final Answer
```

---

## 6. Режимы работы CLI

### 6.1. Interactive Mode

```bash
agent
```

Открывает интерактивный чат.

Функции:

- streaming output;
- history;
- slash commands;
- отображение текущего workspace;
- отображение плана;
- подтверждение опасных действий;
- короткие статусы tool calls.

---

### 6.2. Exec Mode

```bash
agent exec "fix build errors"
```

Одноразовый запуск задачи.

Нужен для:

- автоматизации;
- CI;
- batch tasks;
- интеграции с другими scripts.

---

### 6.3. Plan Mode

```bash
agent plan "refactor CLI renderer"
```

Модель только анализирует и создаёт план. Никаких изменений файлов и команд.

---

### 6.4. Review Mode

```bash
agent review
```

Анализирует текущие изменения:

- `git diff`, если git доступен;
- иначе собственный diff snapshot;
- ищет риски;
- предлагает улучшения;
- не изменяет файлы без отдельной команды.

---

### 6.5. Apply Mode

```bash
agent apply plan.md
```

Применяет заранее созданный план пошагово.

---

## 7. Slash Commands

Минимальный набор:

```text
/help              показать команды
/status            показать workspace, модель, режим, политику
/plan              показать текущий план
/plan clear        очистить план
/compact           сжать историю текущей сессии
/memory show       показать подключенную память
/memory ingest     обновить локальную память проекта
/tools             показать доступные tools
/approvals         показать текущий approval mode
/model             показать текущую модель/API endpoint
/review            ревью текущих изменений
/diff              показать изменения
/undo              откатить последний file patch, если возможно
/exit              выход
```

---

## 8. Approval Policy

Нужно реализовать 4 режима:

```text
ReadOnly
  Можно только читать файлы и отвечать.

WorkspaceWrite
  Можно читать и изменять файлы внутри workspace.
  Shell commands требуют подтверждения, кроме allowlist.

AutoEdit
  Можно читать/писать внутри workspace и запускать безопасные команды из allowlist.

DangerFullAccess
  Максимальный доступ. Только явно включается пользователем.
```

По умолчанию использовать `WorkspaceWrite` или `ReadOnly`.

---

## 9. Sandbox / Workspace Policy

Даже если нет настоящего OS sandbox, должен быть logical sandbox.

### 9.1. Workspace Root

Все операции с файлами проходят через `SafePathResolver`.

Запрещено:

- `../` escape из workspace;
- абсолютные пути вне разрешённых root;
- symlink escape;
- запись в home/system directories;
- запись в `.ssh`, `.git/config`, credentials files;
- удаление больших директорий без approval.

### 9.2. File Risk Levels

```text
Low:
  чтение обычных source/docs файлов

Medium:
  изменение source/docs файлов внутри workspace

High:
  изменение project/config/build files

Critical:
  удаление файлов
  shell commands
  изменение env/config/secrets
  изменение git hooks
  изменение бинарников
```

---

## 10. Command Policy

Shell execution — самый рискованный tool.

### 10.1. Базовые правила

- Команда всегда отображается пользователю перед запуском, если не allowlisted.
- У команды есть timeout.
- stdout/stderr ограничиваются по размеру.
- env должен быть минимальным.
- working directory — только workspace.
- network не гарантируется и не должен использоваться.
- интерактивные команды запрещены.

### 10.2. Allowlist

Пример безопасных команд, если они установлены локально:

```text
dotnet --info
dotnet build --no-restore
dotnet test --no-restore
git status
git diff
git log --oneline -n 20
```

Важно: `dotnet restore` по умолчанию запрещён, потому что может требовать интернет.

### 10.3. Blocklist

```text
rm -rf /
del /s /q C:\
format
curl
wget
powershell Invoke-WebRequest
ssh
scp
git push
git reset --hard
git clean -fdx
dotnet restore
npm install
pip install
```

Blocklist не заменяет allowlist. Любая неизвестная команда требует approval.

---

## 11. Tool System

### 11.1. Интерфейс

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolRisk Risk { get; }
    JsonElement GetSchema();
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken ct);
}
```

### 11.2. Базовые tools

```text
read_file
write_file
list_files
search_text
apply_patch
show_diff
run_command
get_workspace_status
update_plan
memory_search
memory_write
```

### 11.3. Важное правило

LLM не должна получать “сырой shell”, если задачу можно решить специализированным tool.

Лучше:

- `read_file` вместо `cat`;
- `search_text` вместо `grep`;
- `apply_patch` вместо shell redirection;
- `show_diff` вместо shell-specific diff.

Shell — fallback, а не основной механизм.

---

## 12. Patch System

Файлы нельзя перезаписывать хаотично.

Нужно поддержать:

- чтение исходного файла;
- создание proposed patch;
- проверку, что исходный контекст совпадает;
- применение patch;
- сохранение backup/snapshot;
- отображение diff;
- возможность undo последнего изменения.

### 12.1. Patch Format

Внутренне можно использовать простой формат:

```text
FileChange:
  path
  oldText
  newText
  changeType: Create | Modify | Delete
```

Для UI показывать unified diff.

---

## 13. Context Builder

Контекст нельзя собирать как “весь проект в prompt”.

Нужна система:

```text
System Prompt
Developer Policy
Project Instructions
User Task
Session Summary
Relevant Files
Relevant Memory
Recent Tool Observations
Current Plan
```

### 13.1. Источники контекста

1. `AGENTS.md`
2. `README.md`
3. project files, выбранные по релевантности
4. текущий git diff
5. session summary
6. локальная memory
7. tool results

### 13.2. Context Budget

Нужно ввести лимиты:

```text
System/developer policy: reserved
User task: reserved
Recent turns: medium
Files: dynamic
Tool observations: compacted
Memory: only relevant
```

При переполнении:

1. сжимать старую историю;
2. сокращать tool observations;
3. оставлять только relevant file excerpts;
4. не удалять user task и policy.

---

## 14. Project Instructions: AGENTS.md

В корне workspace должен поддерживаться файл:

```text
AGENTS.md
```

Он описывает правила проекта:

```md
# AGENTS.md

## Project
...

## Build
dotnet build --no-restore

## Test
dotnet test --no-restore

## Constraints
- No internet except LLM API.
- Do not run restore/install commands.
- Prefer standard library.
- Keep changes small and reviewable.

## Architecture
...

## Style
...
```

Agent CLI должен автоматически читать этот файл и добавлять в контекст.

---

## 15. Skills System

Нужны локальные skills как markdown-файлы:

```text
/agent/skills/refactor.md
/agent/skills/review.md
/agent/skills/test.md
/agent/skills/offline.md
```

Skill — это не код, а reusable инструкция.

Пример:

```md
# Skill: Offline Refactor

Use this when changing code on a machine without internet.

Rules:
- Do not add new packages.
- Do not run restore/install.
- Prefer standard library.
- Check publish/build assumptions.
- Keep patches small.
```

ContextBuilder выбирает skills по задаче.

---

## 16. Memory System

Память должна быть локальной и простой.

### 16.1. Где хранить

```text
.agent/
  sessions/
    2026-04-28_120000.jsonl
  memory/
    project.md
    decisions.md
    architecture.md
    problems.md
    index.json
  snapshots/
  cache/
```

### 16.2. Типы памяти

```text
Session Log:
  полный JSONL журнал событий

Session Summary:
  краткое summary после compact

Project Memory:
  устойчивые архитектурные решения

Decision Memory:
  важные принятые решения

Problem Memory:
  известные ошибки, ограничения, обходные пути
```

### 16.3. Memory Ingest

Команда:

```bash
agent memory ingest
```

Должна:

1. прочитать важные project docs;
2. обновить `.agent/memory/index.json`;
3. создать/обновить markdown summaries;
4. не индексировать build/bin/obj/.git;
5. не хранить secrets.

### 16.4. Без vector DB

На первом этапе не использовать vector database.

Достаточно:

- keyword scoring;
- file path scoring;
- recency;
- headings;
- explicit links;
- lightweight JSON index.

---

## 17. Event Log

Каждая сессия должна писаться в JSONL:

```json
{"type":"user_message","timestamp":"...","text":"..."}
{"type":"llm_request","timestamp":"...","model":"..."}
{"type":"tool_call","timestamp":"...","tool":"read_file","args":{...}}
{"type":"tool_result","timestamp":"...","success":true}
{"type":"file_change","timestamp":"...","path":"src/A.cs"}
{"type":"approval_request","timestamp":"...","risk":"High"}
{"type":"approval_result","timestamp":"...","approved":true}
{"type":"final_answer","timestamp":"...","text":"..."}
```

Это нужно для:

- отладки;
- воспроизведения;
- памяти;
- compact;
- анализа ошибок агента.

---

## 18. Console UI

CLI должен быть красивым, но без внешних зависимостей.

### 18.1. Поддержать

- ANSI colors, если терминал поддерживает;
- fallback без ANSI;
- streaming assistant text;
- collapsible-like sections через простые блоки;
- status line;
- clear separation of user/model/tool;
- diff rendering;
- markdown-lite rendering.

### 18.2. Markdown-lite

Поддержать минимум:

```text
# headings
- bullets
1. ordered lists
`inline code`
``` code fences ```
> quotes
**bold** можно упростить
```

Не надо полноценный CommonMark parser. Достаточно безопасного renderer.

### 18.3. Windows

Учесть:

- Windows Terminal поддерживает ANSI;
- старый cmd может не поддерживать;
- включить `Console.OutputEncoding = UTF8`;
- аккуратно работать с шириной окна;
- не полагаться на Unix escape sequences без проверки.

---

## 19. LLM Client

### 19.1. Требования

- OpenAI-compatible HTTP client.
- Endpoint задаётся config/env.
- API key из env или local config.
- Streaming желательно.
- Retries с backoff.
- Timeout.
- CancellationToken.
- Логирование request metadata без сохранения секретов.

### 19.2. Config

```json
{
  "llm": {
    "endpoint": "https://api.example.com/v1/responses",
    "apiKeyEnv": "LLM_API_KEY",
    "model": "model-name",
    "temperature": 0.2,
    "maxOutputTokens": 4096
  },
  "agent": {
    "approvalPolicy": "WorkspaceWrite",
    "workspaceRoot": ".",
    "networkDisabledExceptLlm": true
  }
}
```

API key не хранить в session logs.

---

## 20. Prompt Architecture

### 20.1. System Prompt

Должен быть стабильным и коротким:

```text
You are a local coding agent running inside a restricted CLI harness.
You must use tools for file operations.
You must respect sandbox and approval policy.
You must not assume internet access.
Prefer small, reviewable changes.
Before editing, inspect relevant files.
After editing, run available local checks when allowed.
```

### 20.2. Developer Prompt

Динамически добавлять:

- approval mode;
- workspace root;
- available tools;
- command restrictions;
- project instructions;
- current plan;
- context budget.

### 20.3. Tool Result Prompting

Tool observations должны быть компактными:

```text
Tool read_file succeeded.
Path: src/Foo.cs
Content excerpt:
...
```

Большие outputs сокращать.

---

## 21. Error Handling

Нельзя, чтобы агент “умирал” от обычной ошибки.

Обрабатывать:

- LLM timeout;
- malformed JSON/tool call;
- command timeout;
- file locked;
- path denied;
- patch conflict;
- too large file;
- unsupported encoding;
- console resize;
- interrupted operation.

Каждая ошибка должна превращаться в `ToolResult` или `AgentEvent`, а не в crash.

---

## 22. Безопасность

### 22.1. Secrets

Агент не должен:

- печатать API keys;
- отправлять `.env` в LLM без явного разрешения;
- индексировать secrets;
- писать secrets в logs;
- редактировать credential files без approval.

### 22.2. Secret Detection

Простые regex проверки:

```text
api_key
token
secret
password
BEGIN PRIVATE KEY
sk-
ghp_
```

Если файл похож на secret — читать только после approval или показывать redacted excerpt.

---

## 23. Работа с Git

Если git установлен:

- использовать `git status`;
- использовать `git diff`;
- перед изменениями фиксировать baseline;
- после изменений показывать diff.

Запрещено без явного approval:

- `git push`;
- `git reset --hard`;
- `git clean -fdx`;
- изменение remotes;
- изменение hooks.

Если git не установлен, использовать собственные snapshots.

---

## 24. Проверки качества

После изменений агент должен пытаться выполнить локальные проверки:

1. `dotnet build --no-restore`
2. `dotnet test --no-restore`, если есть тесты
3. targeted command из `AGENTS.md`

Если restore нужен — не запускать автоматически. Сообщить пользователю.

---

## 25. Migration Plan для существующего проекта

Codex должен выполнить это по шагам.

### Phase 1 — Audit

1. Найти entry point.
2. Найти прямые вызовы LLM.
3. Найти прямые file writes.
4. Найти shell execution.
5. Найти внешние зависимости.
6. Найти места, где UI смешан с logic.
7. Проверить, можно ли собрать без интернета.

Результат: `docs/AUDIT.md`.

---

### Phase 2 — Extract Core

1. Создать/выделить `AgentCli.Core`.
2. Перенести agent loop в `AgentRunner`.
3. Ввести `AgentEvent`.
4. Ввести `ITool`.
5. Ввести `ILlmClient`.
6. Ввести `IFileSystem` или безопасный file service.
7. Убрать прямые зависимости Core от Console/HTTP/Process.

---

### Phase 3 — Tools

1. Реализовать `ToolRegistry`.
2. Реализовать базовые tools:
   - `read_file`
   - `list_files`
   - `search_text`
   - `apply_patch`
   - `show_diff`
   - `run_command`
3. Все tools должны возвращать structured `ToolResult`.
4. Все tools проходят через policy layer.

---

### Phase 4 — Policy

1. Реализовать `ApprovalPolicy`.
2. Реализовать `SafePathResolver`.
3. Реализовать command allowlist/blocklist.
4. Добавить approval prompt в CLI.
5. Добавить risk classification.

---

### Phase 5 — Context + Memory

1. Реализовать `ContextBuilder`.
2. Читать `AGENTS.md`.
3. Добавить session summary.
4. Добавить `.agent/memory`.
5. Добавить `/compact`.
6. Добавить `/memory ingest`.

---

### Phase 6 — TUI

1. Отделить renderer.
2. Добавить markdown-lite renderer.
3. Добавить diff renderer.
4. Добавить status bar.
5. Добавить fallback plain renderer.
6. Проверить Windows Terminal/cmd.

---

### Phase 7 — Reliability

1. Добавить JSONL event log.
2. Добавить cancellation.
3. Добавить timeout для LLM и commands.
4. Добавить retry для LLM.
5. Добавить ограничение output size.
6. Добавить graceful crash recovery.

---

### Phase 8 — Tests

Минимальные тесты:

```text
SafePathResolverTests
PatchApplierTests
CommandPolicyTests
ContextBuilderTests
MemoryIndexTests
ToolRegistryTests
AgentLoopParsingTests
```

Если тестового проекта нет — создать.

---

## 26. Acceptance Criteria

Проект считается выровненным, если:

- `dotnet publish` создаёт self-contained/folder output без необходимости скачивать зависимости на целевой машине.
- CLI запускается без интернета.
- Единственный сетевой вызов — LLM API.
- Агент умеет работать в `ReadOnly`, `WorkspaceWrite`, `AutoEdit`.
- Файловые операции проходят через tools.
- Shell команды проходят через policy.
- Есть JSONL session logs.
- Есть `AGENTS.md` support.
- Есть local memory `.agent/memory`.
- Есть `/plan`, `/diff`, `/compact`, `/status`.
- Есть безопасный path resolver.
- Есть rollback/undo хотя бы для последнего file patch.
- Build/test команды не запускают restore/install без approval.
- UI не связан напрямую с agent core.

---

## 27. Запрос для Codex

Используй этот запрос в Codex после добавления данного файла в проект:

```text
Read docs/ARCHITECTURE.md and inspect the current C# CLI agent project.

First, do not edit files. Produce docs/AUDIT.md with:
1. current architecture summary,
2. dependency list,
3. places where CLI/UI/LLM/tools/filesystem/shell are coupled,
4. offline-readiness problems,
5. security risks,
6. recommended migration order.

After that, propose a small-step implementation plan.
Do not add external dependencies.
Do not run restore/install commands.
Prefer standard .NET library.
All changes must keep the project buildable.
```

---

## 28. Implementation Rules для Codex

При реализации:

- Не делать большой rewrite за один проход.
- Каждый шаг должен быть маленьким и проверяемым.
- Сначала добавить abstractions, потом переносить реализацию.
- Не добавлять зависимости.
- Не ломать существующий CLI entrypoint без fallback.
- После каждого этапа запускать `dotnet build --no-restore`, если возможно.
- Если build невозможен из-за отсутствия packages, зафиксировать это в отчёте и не запускать restore.
- Все опасные операции описывать перед выполнением.
- Не удалять старый код, пока новый слой не подключён и не проверен.
- Сохранять совместимость с Windows.

---

## 29. Минимальный MVP, если проект нужно быстро оживить

Если времени мало, сделать только это:

1. `ILlmClient`
2. `AgentRunner`
3. `ToolRegistry`
4. `read_file`
5. `search_text`
6. `apply_patch`
7. `run_command` с approval
8. `SafePathResolver`
9. `AGENTS.md` loader
10. JSONL session log
11. `/status`, `/diff`, `/exit`

После этого уже улучшать TUI, память и skills.

---

## 30. Ключевая архитектурная идея

Агент должен быть не набором if/else вокруг LLM, а контролируемой системой:

```text
LLM = reasoning + proposed actions
Harness = control + policy + execution + memory + UI
Tools = narrow capabilities
Workspace = bounded filesystem
Logs = reproducibility
Memory = continuity
Approvals = safety
```

Если это разделение соблюдено, CLI можно постепенно усиливать до уровня полноценного локального coding agent без необходимости внешних зависимостей.
