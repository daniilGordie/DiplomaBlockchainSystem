#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if command -v pwsh >/dev/null 2>&1; then
  exec pwsh -NoProfile -ExecutionPolicy Bypass -File "$SCRIPT_DIR/Invoke-NexusProductSmoke.ps1" "$@"
fi

echo "PowerShell 7 (pwsh) is required to run the product smoke on Linux." >&2
echo "Install pwsh, then rerun: pwsh -File deploy/Invoke-NexusProductSmoke.ps1" >&2
exit 127
