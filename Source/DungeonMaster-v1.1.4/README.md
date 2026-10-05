# Daggerfall Narrator - Dungeon Master v1.1.4

## v1.1.4 - Door/world interaction narration channels

Dungeon Master now listens to both DFU channels used by classic door/world feedback. Real, unowned message boxes such as **"Someone calls out, come in!"** are preserved on screen and narrated as world flavor. Separately, DFU's private mid-screen HUD label is watched through reflection for a narrow whitelist of interaction subtitles such as **"This lock has nothing to fear from you..."**. Mid-screen text is de-duplicated and does not create a second Narrator subtitle over the existing HUD line. NPC-owned dialogue remains excluded.

Dungeon Master is the narration/non-dialogue module of the **Daggerfall Narrator** framework for Daggerfall Unity 1.1.1. NPC dialogue belongs to Daggerfall Narrator - NPC; protagonist speech belongs to Daggerfall Narrator - Player.

The normal native settings path targets **Daggerfall Voice Engine** for local Kokoro/ONNX synthesis. Runtime-generated WAVs are cached locally and automatically trimmed with an LRU-style rolling cache. Legacy Piper configuration remains INI-only for compatibility.

## v1.0.5 voice-selection + settings pass

- Renames the module to **Daggerfall Narrator - Dungeon Master** while preserving its GUID and existing data paths.
- Replaces raw voice-ID entry in normal settings with **US/UK**, **Male/Female**, and **Neutral/Warm/Refined/Energetic/Rugged** delivery selectors mapped to curated English Kokoro voices.
- Adds **Voice Depth** and **Speed** controls. Depth applies a restrained pitch shift while keeping the selected base voice.
- Choose a **Test Sample** and Apply. Changes to accent, gender, delivery, depth, speed, or audio style replay that sample for quick auditioning.
- Reorganizes options into General, Narration, Readables, Voice, Subtitles, and advanced Cache sections.


## v1.0.3 test-feedback fix

The in-options Sound Test now follows DFU's documented live-settings registration flow. Console tests remain available for diagnostics.

## What v1.0.1 narrates

Automatic narration:

- quest/event/environment flavor text
- the Privateer's Hold opening passage ("You wake and look around the room...")
- DFU tutorial prompts/pages
- selected player status text such as "You are healthy", rest/fatigue/condition messages, and location/date status beginning "You are in ..."
- observation-style HUD text beginning "You see a/an/the ..."
- Leveling Inspiration `LVLUP...` quest messages
- character-creation race/region/class descriptions
- the native Warrior/Mage/Rogue constellation class questionnaire
- character biography/background questions
- optional **Climates & Calories** status integration, detected by reflection with no hard dependency

Readable documents:

- Books
- Quest notes / letters / parchments
- Quest Log / Journal
- Player History

These default to **Manual** mode and receive a native DFU-style **READ / STOP** button. Each type can instead be set to Off or Auto in Mod Settings.

## What v1.0.1 deliberately never narrates

Combat-log / combat-resolution text is hard-blocked and there is no setting to enable it. Examples include hit/miss/damage lines, saving throws ("Save against ..."), attack rolls, spell/magic resistance, parry/block/dodge text, and similar combat calculations.

Control-state/mode notices are also blocked, including "Climbing Mode", "Stealth Mode", run/walk/crouch mode notices, and similar toggle text.

NPC dialogue remains excluded. A separate NPC voice mod can be built later without expanding this mod's scope.

## UI behavior

### Flavor/event popups

- **Immersive:** eligible flavor popups are replaced by narration + subtitle and gameplay can continue.
- **Pause:** the popup is visually hidden but remains modal until narration is finished.
- Message-bound narration is cancelled if the associated preserved window closes before speech begins/finishes.

### Readable windows

Books, notes, Quest Log, and History use their normal DFU interfaces. The narrator adds only a small READ/STOP button in Manual mode.

### Subtitles

Three styles are available:

- Modern
- Classic DFU shadowed (default)
- Classic DFU + backdrop

Classic styles use DFU's own default pixel font and shadow colors.

## Voice selection

Normal setup no longer requires knowing a Kokoro voice ID. The **Voice** section selects Accent (US/UK), Gender, and broad Delivery; Dungeon Master maps that combination to a curated voice installed by Shared Kokoro. **Depth** provides a modest ±2.5-semitone range and **Speed** controls cadence.

For advanced diagnostics, `narrator_test_voice <voiceId>` can still audition an explicit Kokoro ID without making raw IDs part of the normal settings workflow. `narrator_voices` reports the shared server voice endpoint and the currently resolved voice.

The shared server exposes `/health` and `/voices` on the configured local Kokoro port.

## Runtime cache

Generated speech goes to:

`<DFU persistent data>/DaggerfallNarrator/Cache/`

Pre-generated/permanent audio goes to:

`<DFU persistent data>/DaggerfallNarrator/VoicePack/`

Only `Cache` is auto-cleaned. `VoicePack` is never deleted by automatic cleanup.

Default cache limit is 1 GB. Cleanup removes least-recently-used WAVs and trims toward about 85% of the limit while narration is idle.

Console commands:

- `narrator_cache_status`
- `narrator_clear_cache`

## External text rules

On first run the mod creates:

`<DFU persistent data>/DaggerfallNarrator/TextFilters.txt`

Rules use:

```text
ALLOW:some phrase
BLOCK:some phrase
```

Built-in combat and mode-toggle blocking always wins; a user ALLOW rule cannot make combat-log text speak.

## Compatibility API for other DFU mods

Other mods can submit narrator-worthy text without UI scraping:

```csharp
DaggerfallNarrator.DaggerfallNarratorMod.Speak("You feel a cold draft from the passage ahead.");
```

Or:

```csharp
DaggerfallNarrator.DaggerfallNarratorMod.Speak(text, "my-mod-source-key", true);
```

This API is for narrator/non-dialogue text, not NPC dialogue.

# Build instructions (DFU 1.1.1)

You need:

1. Daggerfall Unity **1.1.1 source project**
2. Unity **2019.4.40f1**
3. This package

Copy:

`Assets/Game/Mods/DaggerfallNarrator/`

into the same path inside your DFU source project.

Open the source project in Unity 2019.4.40f1. Wait for compilation and make sure the Console has **0 red errors**.

Then:

1. `Daggerfall Tools -> Mod Builder`
2. Open `Assets/Game/Mods/DaggerfallNarrator/DaggerfallNarrator.dfmod.json`
3. Expand Files and verify `DaggerfallNarrator.cs` and `modsettings.json` are present
4. Enable **Precompiled**
5. Click **Build Mod**
6. Copy the Windows `DaggerfallNarrator.dfmod` into your playable game's `DaggerfallUnity_Data/StreamingAssets/Mods/`
7. Enable the mod in the DFU launcher

This archive is build-ready source, not a prebuilt Unity AssetBundle. The actual Unity compile remains the final API/compatibility check.

# Daggerfall Voice Engine setup

Current releases use **Daggerfall Voice Engine**, not the legacy Python SharedKokoro setup. A finished player release is self-contained. Install the engine under:

`DaggerfallUnity_Data/StreamingAssets/DaggerfallVoiceEngine/`

The Narrator modules health-check and auto-launch the engine. Players do not install Python, pip, .NET, eSpeak, or Kokoro model files separately. `Daggerfall Voice Engine.exe` (or the corresponding macOS/Linux binary) may be launched manually only for troubleshooting.

# First in-game test

Launch normal Daggerfall Unity. Daggerfall Voice Engine should start automatically when Kokoro narration is enabled.

Use `Mods -> Daggerfall Narrator -> Settings` and keep the defaults for the first test.

Open the DFU console (`~` / backquote on a typical US keyboard) and run:

```text
narrator_test
```

Then try:

```text
narrator_say You hear something moving in the darkness.
```

Useful commands:

- `narrator_status`
- `narrator_voices`
- `narrator_reload`
- `narrator_cache_status`
- `narrator_clear_cache`

F9 skips current narration. F10 temporarily toggles narration for the current session.

# Recommended defaults

- Enabled: On
- Mode: Immersive
- Message-box timing: Follow window
- Readables: Manual / READ-STOP
- Subtitle style: Classic DFU shadowed
- Kokoro voice: `bm_george`
- Kokoro language: `auto`
- Kokoro speed: 95%
- Cache: automatic, 1 GB
- Combat narration: unavailable by design

# Climates & Calories

When **Narrate Climates & Calories** is enabled, the narrator attempts to detect that mod's extra status message box by reflection and also permits common hunger/thirst/temperature condition messages through the HUD allowlist. There is no compile-time dependency; if the mod is not installed, this path does nothing.

Because third-party mods can change internally, `TextFilters.txt` and the public Speak API are also provided as compatibility escape hatches.


## v1.0.2 voice-test QoL

The **Sound Test** settings section can audition the current narrator voice against atmosphere, readable, status, tutorial, and opening-narration samples. Console users can also run `narrator_test_voice <voiceId>` without permanently changing the configured voice, or `narrator_test_preset atmosphere|book|status|tutorial|opening`.

## v1.0.2 Kokoro Voice Framework sound tests

Narrator now follows the same audition philosophy as NPCVO and PlayerVO. The native Mod Settings **Sound Test** section can audition representative atmosphere, readable/book, status, tutorial, and opening lines without waiting for the corresponding game event.

Console helpers:

- `narrator_test_voice <voiceId>` — audition an arbitrary Kokoro voice without changing the configured narrator voice.
- `narrator_test_preset <atmosphere|book|status|tutorial|opening>` — audition a representative Narrator content style.

All tests still use the same shared local Kokoro server as the other framework modules.

## v1.1.0 - Daggerfall Voice Engine
Runtime Kokoro speech now targets **Daggerfall Voice Engine** at localhost. The mod can auto-launch the bundled engine from `DaggerfallUnity_Data/StreamingAssets/DaggerfallVoiceEngine/`, participates in the shared cross-module speech queue, supports Player-routed observations, and uses reactive-only synthesis/cache behavior.


## v1.1.1 - Quest dialogue ownership guard
When Daggerfall Narrator - NPC reports active/recent quest dialogue ownership, Dungeon Master leaves immediate generic quest follow-up messages alone instead of replacing them as Narrator flavor.


## v1.1.3 - Tutorial ownership + load stability

- Narrates speakerless quest-script Prompt/PromptMulti text as preserved system/flavor UI when NPCVO does not own a real NPC conversation. This closes the new-game/tutorial gap without converting system prompts into fake NPC dialogue.
- Broadens Privateer's Hold/opening detection so early-game intro text is less dependent on one exact wording.
- Adds a deliberately narrow HUD-flavor path for classic world quips (for example lock/door observations) that never appear as message boxes.
- PlayerEntity/save transitions now hard-reset narration queues, modal/source tracking, audio/subtitle state, duplicate guards, and all DungeonMaster-owned Voice Engine turns.
- Context-change cleanup now cancels the entire DungeonMaster module at the Voice Engine, preventing stale narration ownership from surviving save/character changes.
