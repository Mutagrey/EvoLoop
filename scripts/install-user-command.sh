#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
run_script="$repo_root/scripts/run-agent.sh"

if [[ ! -x "$run_script" ]]; then
  echo "run-agent.sh not found or not executable: $run_script" >&2
  exit 1
fi

marker_start="# >>> EvoLoop agent >>>"
marker_end="# <<< EvoLoop agent <<<"

read -r -d '' snippet <<SNIPPET || true
$marker_start
agent() {
  "$run_script" --workspace "\$PWD" "\$@"
}
$marker_end
SNIPPET

updated=0
for rc in "$HOME/.zshrc" "$HOME/.bashrc"; do
  if [[ ! -f "$rc" ]]; then
    touch "$rc"
  fi

  if grep -Fq "$marker_start" "$rc"; then
    continue
  fi

  printf "\n%s\n" "$snippet" >> "$rc"
  updated=1
  echo "Updated $rc"
done

if [[ $updated -eq 0 ]]; then
  echo "Shell profiles already configured."
else
  echo "Installed user-level 'agent' shell function (no admin needed)."
fi

echo "Open a new terminal, or run:"
echo "  source ~/.zshrc"
