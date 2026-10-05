# Daggerfall Narrator - Dungeon Master v1.0.4.1

- Renames the module while preserving its GUID and persistent configuration paths.
- Replaces exact voice-ID entry in native settings with curated Accent, Gender, and Delivery selectors.
- Adds Voice Depth and keeps Speed as an immediately auditionable delivery control.
- Selected Sound Test sample replays when voice controls are applied.
- Tidies native options into General, Narration, Readables, Voice, Subtitles, and advanced Cache sections.
- Shared Kokoro remains the normal framework backend; explicit voice IDs remain available only through advanced console/INI workflows.

# Daggerfall Narrator v1.0.3

- Fixes DFU live mod-settings registration by assigning LoadSettingsCallback and then invoking mod.LoadSettings(), matching DFU's documented pattern.
- Repairs the in-options Sound Test preset so changing it can actually reach the running Narrator module.
- Keeps console voice tests unchanged as a fallback diagnostic path.

# 1.0.2

- Adds a native Mod Settings Sound Test section with representative atmosphere, book, status, tutorial, and opening samples.
- Adds `narrator_test_voice <voiceId>` for one-off Kokoro voice auditions without changing the configured narrator voice.
- Adds `narrator_test_preset <name>` for console-based content-style tests.
- Cache identity now includes per-test voice overrides.

# Daggerfall Narrator v1.0.1.1

## Hotfix
- Fixes a v1.0.1 regression where the new Classic DFU subtitle renderer could initialize before the gameplay HUD existed during character creation, aborting the per-frame detector and preventing race/region/class description narration.
- Class questionnaire detection now searches the DFU UI window stack rather than requiring `CreateCharClassQuestions` to be the absolute top window. This is more tolerant of UI overlays/mods.
- Added defensive subtitle-renderer error isolation so a presentation failure cannot disable narration detection.
- Added optional debug logging for detected class questionnaire text.

# Daggerfall Narrator v1.0.1

## v1.0.1 feature-completion pass

- Added native DFU-style READ/STOP controls for Books, quest notes/letters, Quest Log, and Player History.
- Added Off / Manual / Auto modes for each readable category; Manual is the default.
- Added explicit narration for the built-in Warrior/Mage/Rogue constellation class questionnaire.
- Added explicit player location/date status handling for `I`-key readouts beginning "You are in ...".
- Reworked HUD narration around an allowlist-first model.
- Combat-log/combat-resolution narration is now hard-blocked with no user-facing enable option.
- Added hard blocks for saving throws (`Save against ...`), hit/miss/damage/roll/resistance language, and similar combat calculations.
- Added hard blocks for mode-toggle notices such as Climbing Mode and Stealth Mode.
- Added observation exception for `You see a/an/the ...` messages.
- Added optional Climates & Calories compatibility with reflection-only detection and status wording support.
- Added Classic DFU shadowed and Classic DFU + backdrop subtitle styles using DFU's own default pixel font.
- Changed Kokoro voice selection to a free-form voice ID so compatible voices are not limited to a curated dropdown.
- Added Kokoro `/voices` endpoint and `narrator_voices` helper command.
- Added `TextFilters.txt` external ALLOW/BLOCK rules. Built-in combat/mode blocking always wins.
- Added a small public `DaggerfallNarratorMod.Speak()` API for other DFU mods to submit narrator-worthy text.
- Narration queue is cleared when leaving active gameplay so stale speech does not bleed into menu/save transitions.
- Retained v0.7 message-bound cancellation, low-latency first-chunk generation/prefetch, warm-up, Leveling Inspiration support, character-creation descriptions/questions, tutorial/opening narration, and bounded rolling cache.
- NPC dialogue remains deliberately outside this mod's scope.
