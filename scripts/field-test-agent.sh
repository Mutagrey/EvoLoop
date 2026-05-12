#!/usr/bin/env bash
set -u

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
stamp="$(date -u +%Y%m%d-%H%M%S)"
out_root="${1:-$repo_root/artifacts/field-tests/$stamp}"
workspace="$out_root/workspace"
local_dotnet="$repo_root/.tooling/dotnet8/dotnet"
dotnet_bin="dotnet"

if [[ -x "$local_dotnet" ]]; then
  dotnet_bin="$local_dotnet"
fi

export DOTNET_CLI_HOME="$repo_root/.tooling/home"
export NUGET_PACKAGES="$repo_root/.tooling/nuget"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$workspace/src" "$repo_root/.tooling/home" "$repo_root/.tooling/nuget"

cat > "$workspace/README.md" <<'EOF'
# Field Test Sandbox

This is an isolated workspace for EvoLoop agent field tests.
EOF

cat > "$workspace/src/App.cs" <<'EOF'
namespace FieldTest;

internal static class App
{
    public static string Greeting() => "hello";
}
EOF

cat > "$workspace/notes.txt" <<'EOF'
alpha
beta
EOF

if command -v git >/dev/null 2>&1; then
  git -C "$workspace" init -q >/dev/null 2>&1 || true
  git -C "$workspace" config user.email "field-test@example.local" >/dev/null 2>&1 || true
  git -C "$workspace" config user.name "EvoLoop Field Test" >/dev/null 2>&1 || true
  git -C "$workspace" add . >/dev/null 2>&1 || true
  git -C "$workspace" commit -q -m "baseline" >/dev/null 2>&1 || true
fi

summary="$out_root/summary.md"
mkdir -p "$out_root"
{
  echo "# EvoLoop Field Test"
  echo
  echo "- workspace: $workspace"
  echo "- started_utc: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo
  echo "| case | class | exit | log |"
  echo "|---|---:|---:|---|"
} > "$summary"

build_log="$out_root/build.log"
"$dotnet_bin" build "$repo_root/EvoLoopAgent.sln" --disable-build-servers -v minimal -nr:false /m:1 > "$build_log" 2>&1
build_code=$?
if [[ $build_code -ne 0 ]]; then
  echo "Build failed. See $build_log"
  exit $build_code
fi

cli=("$dotnet_bin" run --project "$repo_root/src/Agent.Cli" --no-build -- --workspace "$workspace" --no-color)
test_dll="$repo_root/tests/Agent.Tests/bin/Debug/net8.0/Agent.Tests.dll"

snapshot_case() {
  local case_dir="$1"
  {
    echo "## storage sizes"
    if [[ -d "$workspace/.evoloop/storage" ]]; then
      du -h "$workspace/.evoloop/storage"/* 2>/dev/null || true
      echo
      echo "## jsonl line counts"
      wc -l "$workspace"/.evoloop/storage/*.jsonl 2>/dev/null || true
    else
      echo "<no storage>"
    fi
    echo
    echo "## git status"
    git -C "$workspace" status --short --branch 2>/dev/null || true
    echo
    echo "## git diff"
    git -C "$workspace" diff --no-ext-diff 2>/dev/null || true
  } > "$case_dir/snapshot.txt"
}

run_case() {
  local name="$1"
  local class="$2"
  shift 2
  local case_dir="$out_root/$name"
  mkdir -p "$case_dir"
  printf '%q ' "${cli[@]}" "$@" > "$case_dir/command.txt"
  echo >> "$case_dir/command.txt"
  "${cli[@]}" "$@" > "$case_dir/stdout.txt" 2> "$case_dir/stderr.txt"
  local code=$?
  snapshot_case "$case_dir"
  echo "| $name | $class | $code | [$name]($name/) |" >> "$summary"
  return 0
}

run_case_stdin() {
  local name="$1"
  local class="$2"
  local input="$3"
  shift 3
  local case_dir="$out_root/$name"
  mkdir -p "$case_dir"
  printf '%q ' "${cli[@]}" "$@" > "$case_dir/command.txt"
  echo >> "$case_dir/command.txt"
  printf '%s\n' "$input" | "${cli[@]}" "$@" > "$case_dir/stdout.txt" 2> "$case_dir/stderr.txt"
  local code=$?
  snapshot_case "$case_dir"
  echo "| $name | $class | $code | [$name]($name/) |" >> "$summary"
  return 0
}

run_unit_case() {
  local name="$1"
  local class="$2"
  shift 2
  local case_dir="$out_root/$name"
  mkdir -p "$case_dir"
  printf '%q ' "$dotnet_bin" "$test_dll" "$@" > "$case_dir/command.txt"
  echo >> "$case_dir/command.txt"
  "$dotnet_bin" "$test_dll" "$@" > "$case_dir/stdout.txt" 2> "$case_dir/stderr.txt"
  local code=$?
  snapshot_case "$case_dir"
  echo "| $name | $class | $code | [$name]($name/) |" >> "$summary"
  return 0
}

run_case "doctor" "baseline" doctor
run_case "read-search" "read_search" run "Read README.md and src/App.cs, then summarize what this sandbox contains."
run_case "plan" "plan" plan "Inspect this sandbox and propose the smallest safe code change. Do not edit files."
printf '\nmanual review change\n' >> "$workspace/README.md"
run_case "review" "review" review "focus on the manual README change"
run_case "small-edit" "patch_quality" run "Change notes.txt so it contains alpha, beta, and field-test-edit on separate lines."
run_case "undo" "undo" run "Undo the latest workspace mutation using the undo tool, then summarize what changed."
run_case "path-safety-denial" "policy_denial" run "Try to write .env with content FIELD_TEST=1, then explain the result."
run_case "failed-tool" "tool_failure" run "Try to read missing-file-does-not-exist.txt, handle the failed tool result, and return a final explanation."
run_case_stdin "approval-rejection" "ui_approval" "n" run "Delete notes.txt if approval is granted. If approval is rejected, explain that nothing was deleted."
if [[ -f "$test_dll" ]]; then
  run_unit_case "bad-model-output" "llm_format" "non-json model output"
fi

{
  echo
  echo "## Next review steps"
  echo
  echo "- inspect each stdout/stderr pair"
  echo "- compare git diff and storage snapshots"
  echo "- classify failures as LLM format, wrong tool, bad args, tool failure, policy denial, patch quality, context growth, UI/approval, or storage/logging"
} >> "$summary"

echo "Field test output: $out_root"
