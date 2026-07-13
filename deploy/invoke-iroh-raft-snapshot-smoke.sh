#!/usr/bin/env bash
set -euo pipefail

timeout_seconds=600
keep_containers=0
skip_cluster_start=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --timeout-seconds)
      timeout_seconds="$2"
      shift 2
      ;;
    --keep-containers-on-failure)
      keep_containers=1
      shift
      ;;
    --skip-cluster-start)
      skip_cluster_start=1
      shift
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cmd=("$script_dir/Invoke-IrohRaftSnapshotSmoke.ps1" -TimeoutSeconds "$timeout_seconds")
if [[ "$keep_containers" == "1" ]]; then
  cmd+=(-KeepContainersOnFailure)
fi
if [[ "$skip_cluster_start" == "1" ]]; then
  cmd+=(-SkipClusterStart)
fi

pwsh -NoLogo -NoProfile -File "${cmd[@]}"
