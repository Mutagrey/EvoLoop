#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
run_script="$repo_root/scripts/run-agent.sh"
cli_script="$repo_root/scripts/run-agent-cli.sh"

if [[ ! -x "$run_script" ]]; then
  echo "run-agent.sh not found or not executable: $run_script" >&2
  exit 1
fi
if [[ ! -x "$cli_script" ]]; then
  echo "run-agent-cli.sh not found or not executable: $cli_script" >&2
  exit 1
fi

marker_start="# >>> EvoLoop agent >>>"
marker_end="# <<< EvoLoop agent <<<"

read -r -d '' snippet <<SNIPPET || true
$marker_start
agent() {
  "$run_script" --workspace "\$PWD" "\$@"
}
agent-cli() {
  "$cli_script" --workspace "\$PWD" "\$@"
}
$marker_end
SNIPPET

replace_block() {
  local rc="$1"
  local tmp
  tmp="$(mktemp "$rc.tmp.XXXXXX")"
  local in_block=0
  local wrote=0

  while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" == *"$marker_start"* ]]; then
      if [[ $wrote -eq 0 ]]; then
        printf '%s\n' "$snippet" >> "$tmp"
      fi
      in_block=1
      wrote=1
      continue
    fi

    if [[ "$line" == *"$marker_end"* ]]; then
      in_block=0
      continue
    fi

    if [[ $in_block -eq 0 ]]; then
      printf '%s\n' "$line" >> "$tmp"
    fi
  done < "$rc"

  if [[ $wrote -eq 0 ]]; then
    if [[ -s "$tmp" ]]; then
      printf '\n' >> "$tmp"
    fi
    printf '%s\n' "$snippet" >> "$tmp"
  fi

  if cmp -s "$rc" "$tmp"; then
    rm -f "$tmp"
    return 1
  fi

  cp "$rc" "$rc.evoloop.bak.$(date +%Y%m%d%H%M%S)"
  mv "$tmp" "$rc"
  return 0
}

updated=0
for rc in "$HOME/.zshrc" "$HOME/.bashrc"; do
  if [[ ! -f "$rc" ]]; then
    touch "$rc"
  fi

  if replace_block "$rc"; then
    updated=1
    echo "Updated $rc"
  fi
done

if [[ $updated -eq 0 ]]; then
  echo "Shell profiles already up to date."
else
  echo "Installed user-level 'agent' and 'agent-cli' shell functions (no admin needed)."
fi

echo "Open a new terminal, or run:"
echo "  source ~/.zshrc"
