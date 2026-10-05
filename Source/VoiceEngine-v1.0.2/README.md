# Daggerfall Voice Engine 1.0.2

Cross-platform local Kokoro/ONNX speech backend for Daggerfall Narrator: Dungeon Master, NPC, and Player.

## Player install

A finished release is self-contained. Extract its contents into the Daggerfall Unity directory. The mods health-check and auto-launch the engine from:

`DaggerfallUnity_Data/StreamingAssets/DaggerfallVoiceEngine/`

No Python, pip, .NET, eSpeak, uv, model download, voice download, or separate Kokoro setup is required for players.

The engine uses one shared speech-turn coordinator across all three modules and a reactive-only WAV cache. It does not pre-synthesize speculative lines.

## Maintainer build

Windows: run `Builder/BuildRelease.cmd`.

macOS: run `Builder/BuildRelease.command [osx-arm64|osx-x64]`.

Linux: run `Builder/BuildRelease.sh [linux-x64|linux-arm64]`.

The builder package bootstraps its own private .NET 8 SDK and uv installation, downloads the Kokoro ONNX model, downloads/converts the CC0 `am_granite` voice, restores KokoroSharp into a private NuGet cache, explicitly packages KokoroSharp's complete official voice catalog, publishes a self-contained runtime, performs a two-catalog runtime self-test, and writes the redistributable archive under `Output/`.

If compiled `.dfmod` files are placed in `InputMods/` before the build, they are included under `DaggerfallUnity_Data/StreamingAssets/Mods/`.

## v1.0.2 builder/runtime corrections

- Fixed the Windows uv bootstrap so `uv.exe` is never copied onto itself.
- Added the required `KokoroSharp.Core` namespace import.
- Official Kokoro voices are copied explicitly from the builder's private NuGet cache into `Assets/voices`.
- `DaggerfallVoiceEngine.json` now has an explicit `OfficialVoicesPath`.
- The release self-test requires both official `af_heart` and custom `am_granite` synthesis to succeed.
- Missing optional voice IDs degrade to available blend members/fallback instead of producing repeated HTTP 500 errors.
- `GET /` now returns friendly engine status rather than a confusing 404.
- `GET /health` reports the loaded voice count.

## HTTP API

- `GET /`
- `GET /health`
- `GET /status`
- `GET /version`
- `GET /voices`
- `POST /presence/register`
- `POST /turn/acquire`
- `GET /turn/{id}`
- `POST /turn/complete`
- `POST /turn/cancel`
- `POST /turn/cancel-module`
- `POST /synthesize`
- `POST /shutdown`

The local synthesis request fields remain supported, including voice blends, speed, pitch, fantasy DSP, CD-ROM/DOS output styles, and emotion hints.
