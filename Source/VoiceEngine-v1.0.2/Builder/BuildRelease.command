#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
RID="${1:-}"
if [[ -z "$RID" ]]; then
  case "$(uname -m)" in arm64) RID=osx-arm64;; x86_64) RID=osx-x64;; *) echo "Unsupported Mac architecture"; exit 1;; esac
fi
bash ./build_unix.sh "$RID"
echo; read -n 1 -s -r -p "Build complete. Press any key to close."
