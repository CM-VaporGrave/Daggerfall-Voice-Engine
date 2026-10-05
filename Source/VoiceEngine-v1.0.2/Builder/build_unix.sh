#!/usr/bin/env bash
set -euo pipefail

VERSION="1.0.2"
RID="${1:-linux-x64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOOLS="$ROOT/.tools"
OUT="$ROOT/Output"
STAGE="$ROOT/Stage"
SRC="$ROOT/src/DaggerfallVoiceEngine"
ASSETS="$ROOT/BuildAssets"
NUGET_PACKAGES="$TOOLS/nuget-packages"

export NUGET_PACKAGES
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

mkdir -p "$TOOLS" "$OUT" "$STAGE" "$ASSETS" "$NUGET_PACKAGES"

DOTNET="$TOOLS/dotnet/dotnet"
if [[ ! -x "$DOTNET" ]]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TOOLS/dotnet-install.sh"
  bash "$TOOLS/dotnet-install.sh" --channel 8.0 --install-dir "$TOOLS/dotnet" --no-path
fi

UV="$TOOLS/uv"
if [[ ! -x "$UV" ]]; then
  curl -LsSf https://astral.sh/uv/install.sh | env UV_INSTALL_DIR="$TOOLS" sh
fi
[[ -x "$UV" ]] || { echo "uv bootstrap failed: $UV"; exit 1; }

MODEL="$ASSETS/kokoro.onnx"
GRANITE_PT="$ASSETS/am_granite.pt"
GRANITE_NPY="$ASSETS/am_granite.npy"

[[ -f "$MODEL" ]] || curl -fL https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx -o "$MODEL"
[[ -f "$GRANITE_PT" ]] || curl -fL https://raw.githubusercontent.com/n33kos/kokoro-voices/main/voices/am_granite.pt -o "$GRANITE_PT"
[[ -f "$GRANITE_NPY" ]] || "$UV" run --python 3.12 --with 'torch>=2.2,<3' --with 'numpy>=1.26,<3' "$ROOT/Builder/convert_granite.py" "$GRANITE_PT" "$GRANITE_NPY"

PROJECT="$SRC/DaggerfallVoiceEngine.csproj"
PUBLISH="$STAGE/publish"
PKG="$STAGE/package"
rm -rf "$PUBLISH" "$PKG"
mkdir -p "$PUBLISH"

"$DOTNET" restore "$PROJECT"
"$DOTNET" publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$PUBLISH"

# Package official Kokoro voices from the builder's private NuGet cache.
HEART="$(find "$NUGET_PACKAGES" -type f -path '*/kokorosharp/*/content/voices/af_heart.npy' -print -quit)"
[[ -n "$HEART" ]] || { echo "Could not locate KokoroSharp official voice catalog after restore."; exit 1; }
OFFICIAL_VOICES="$(dirname "$HEART")"
OFFICIAL_COUNT="$(find "$OFFICIAL_VOICES" -maxdepth 1 -type f -name '*.npy' | wc -l | tr -d ' ')"
[[ "$OFFICIAL_COUNT" -ge 20 ]] || { echo "Official Kokoro voice catalog looks incomplete: $OFFICIAL_COUNT files"; exit 1; }

ENGINE="$PKG/DaggerfallUnity_Data/StreamingAssets/DaggerfallVoiceEngine"
MODS="$PKG/DaggerfallUnity_Data/StreamingAssets/Mods"
OFFICIAL_DEST="$ENGINE/Assets/voices"
CUSTOM_DEST="$ENGINE/Assets/voices-custom"

mkdir -p "$ENGINE" "$MODS" "$OFFICIAL_DEST" "$CUSTOM_DEST"
cp -a "$PUBLISH/." "$ENGINE/"
cp "$MODEL" "$ENGINE/Assets/kokoro.onnx"
cp -a "$OFFICIAL_VOICES/." "$OFFICIAL_DEST/"
cp "$GRANITE_NPY" "$CUSTOM_DEST/am_granite.npy"
cp "$SRC/DaggerfallVoiceEngine.json" "$ENGINE/"
cp -a "$ROOT/Licenses" "$ENGINE/"

LICENSES="$ENGINE/Licenses"
curl -fsSL https://raw.githubusercontent.com/Lyrcaxis/KokoroSharp/main/LICENSE -o "$LICENSES/KokoroSharp-MIT.txt"
curl -fsSL https://raw.githubusercontent.com/microsoft/onnxruntime/main/LICENSE -o "$LICENSES/ONNX-Runtime-MIT.txt"
curl -fsSL https://www.apache.org/licenses/LICENSE-2.0.txt -o "$LICENSES/Kokoro-Apache-2.0.txt"
curl -fsSL https://creativecommons.org/publicdomain/zero/1.0/legalcode.txt -o "$LICENSES/Granite-CC0-1.0.txt"

find "$ROOT/InputMods" -maxdepth 1 -name '*.dfmod' -exec cp {} "$MODS/" \; 2>/dev/null || true

EXE="$ENGINE/Daggerfall Voice Engine"
chmod +x "$EXE"

[[ -f "$ENGINE/Assets/kokoro.onnx" ]] || { echo "Packaged kokoro.onnx missing"; exit 1; }
[[ -f "$OFFICIAL_DEST/af_heart.npy" ]] || { echo "Packaged official voice af_heart.npy missing"; exit 1; }
[[ -f "$CUSTOM_DEST/am_granite.npy" ]] || { echo "Packaged custom voice am_granite.npy missing"; exit 1; }

"$EXE" --self-test

cat > "$PKG/README-Daggerfall-Voice-Engine.txt" <<'TXT'
DAGGERFALL VOICE ENGINE - PLAYER INSTALL

Extract this archive into your Daggerfall Unity folder and preserve folders.
Enable one or more Daggerfall Narrator modules and launch Daggerfall Unity normally.
The mods start Daggerfall Voice Engine automatically.

Players do not install Python, pip, .NET, eSpeak, uv, or model/voice files separately.

Troubleshooting:
- http://127.0.0.1:5000/ shows engine status.
- http://127.0.0.1:5000/health shows health/queue status.
- Daggerfall Voice Engine.log is written beside the executable.
TXT

if command -v zip >/dev/null 2>&1; then
  ZIP="$OUT/Daggerfall-Narrator-Daggerfall-Voice-Engine-$RID-v$VERSION.zip"
  rm -f "$ZIP"
  (cd "$PKG" && zip -qry "$ZIP" .)
  echo "READY TO REDISTRIBUTE: $ZIP"
else
  TAR="$OUT/Daggerfall-Narrator-Daggerfall-Voice-Engine-$RID-v$VERSION.tar.gz"
  tar -C "$PKG" -czf "$TAR" .
  echo "READY TO REDISTRIBUTE: $TAR"
fi

echo "Official Kokoro voices packaged: $OFFICIAL_COUNT"
