#!/usr/bin/env bash
# Linux/macOS: Restore, Build (inkl. WPF-App über EnableWindowsTargeting – sie wird nicht gestartet) und alle Tests.
# Aufruf:  build/build.sh                 # Release
#          CONFIGURATION=Debug build/build.sh
#          build/build.sh --filter "FullyQualifiedName~Fixture"   # zusätzliche Argumente gehen an dotnet test
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
configuration="${CONFIGURATION:-Release}"
results="${RESULTS_DIR:-$root/TestResults}"

echo "==> restore"
dotnet restore Musterdepot.slnx

echo "==> build ($configuration)"
dotnet build Musterdepot.slnx --configuration "$configuration" --no-restore

status=0
for proj in tests/*/*.csproj; do
  name="$(basename "$proj" .csproj)"
  echo "==> test $name"
  dotnet test "$proj" --configuration "$configuration" --no-build \
    --logger "trx;LogFileName=$name.trx" --results-directory "$results" "$@" || status=1
done

if [ "$status" -ne 0 ]; then
  echo "Tests fehlgeschlagen – Details in $results" >&2
fi
exit "$status"
