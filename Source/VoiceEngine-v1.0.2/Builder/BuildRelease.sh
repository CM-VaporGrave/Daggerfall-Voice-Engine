#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
RID="${1:-linux-x64}"
bash ./build_unix.sh "$RID"
