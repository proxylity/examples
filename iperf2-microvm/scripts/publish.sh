#!/usr/bin/env bash
#
# Compiles the app to a single native Linux executable (NativeAOT) and stages it, with its
# Dockerfile, in build/image/. That folder is what the MicroVM image is built from.
#
# Usage: scripts/publish.sh
#   RID=linux-x64 (default) or linux-arm64
#   WORK_DIR=~/iperf2-microvm   build in a Linux path (recommended when the repo is on /mnt/c)
#
# Needs the .NET 10 SDK and a C toolchain/linker (clang, or gcc via CPP_LINKER=gcc).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${RID:-linux-x64}"
WORK_DIR="${WORK_DIR:-$ROOT}"
CPP_LINKER="${CPP_LINKER:-$(command -v clang >/dev/null && echo clang || echo gcc)}"
STAGE="$ROOT/build/image"

if [[ "$WORK_DIR" != "$ROOT" ]]; then
  mkdir -p "$WORK_DIR"
  tar -C "$ROOT" --exclude=bin --exclude=obj -cf - src | tar -C "$WORK_DIR" -xf -
fi

echo "Publishing NativeAOT ($RID, linker: $CPP_LINKER)..."
(cd "$WORK_DIR" && dotnet publish src/Iperf2.MicroVm -c Release -r "$RID" \
  -p:CppCompilerAndLinker="$CPP_LINKER" -o "$WORK_DIR/publish/$RID" --nologo -v q)

rm -rf "$STAGE"
mkdir -p "$STAGE"
cp "$WORK_DIR/publish/$RID/iperf2-microvm" "$STAGE/"
cp "$ROOT/src/Iperf2.MicroVm/Dockerfile" "$STAGE/"
chmod +x "$STAGE/iperf2-microvm"

# The container base (al2023-minimal) has glibc 2.34. A binary linked on a newer distro can still
# run there as long as it only asks for symbols up to that version.
MAX_GLIBC=$(objdump -T "$STAGE/iperf2-microvm" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)
echo "Highest glibc symbol version required: ${MAX_GLIBC}"
if [[ "$(printf '%s\n' "$MAX_GLIBC" GLIBC_2.34 | sort -V | tail -1)" != "GLIBC_2.34" ]]; then
  echo "This binary needs a newer glibc than al2023 provides. Build on an older distro (or in an al2023 container)." >&2
  exit 1
fi

echo "Staged in $STAGE:"
ls -la "$STAGE"
