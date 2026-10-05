# Daggerfall Narrator - Player v0.3.7 - Daggerfall Voice Engine module
## v0.3.7 - Stable chargen flow + packaged voice filters

Character creation now uses an explicit, non-cancelable **Personality -> Voice -> Summary** flow so closing a custom PlayerVO screen cannot unwind DFU back to the beginning. Personality selection has an explicit **Use Personality** button, and restarting DFU character creation resets PlayerVO's per-summary step state correctly.

The Voice screen still offers every voice physically packaged with Daggerfall Voice Engine, but now adds **Accent: All / US / UK** and **Gender: All / Female / Male** as browsing filters only. They never forbid a voice: set both to **All** to cycle the complete packaged catalog. Gender initially follows the character being created when DFU exposes it, preventing an accidental male/female mismatch while preserving full player choice. Preview, Depth, Speed, Quality, and per-character voice persistence are unchanged.

## v0.3.6 - Character-creation voice browser + live preview

After the personality step, character creation can now open a dedicated Voice panel. Its catalog is built only from `.npy` files actually present under `DaggerfallUnity_Data/StreamingAssets/DaggerfallVoiceEngine/Assets/voices/` and `voices-custom/`. Cycle through the packaged catalog, adjust Depth, Speed and Clean/CD-ROM/DOS quality with Daggerfall-style arrow controls, and press **Preview Voice** to hear the exact result before selecting **Use Voice**. The chosen exact voice and processing settings are persisted per character. No remote catalog is queried and no unbundled voice is offered.


PlayerVO is the player-character speech module for **Daggerfall Unity 1.1.1**. It uses **Daggerfall Voice Engine** for local Kokoro/ONNX synthesis and cross-module speech arbitration. It is deliberately deterministic: there is **no LLM, no generated quest writing, and no NPC logic replacement**.


## v0.2.0 birthsign personality + final presentation pass

- **Voice and Personality are separate settings.** Voice controls Kokoro rendering; Personality controls authored worldview and never selects the voice.
- Replaces the previous tone list with all thirteen Elder Scrolls birthsigns as roleplay archetypes. Twelve are comparatively talkative; **The Apprentice - Agent** is the sparse newcomer archetype, speaking little automatically while still voicing deliberate choices and a few mission-critical reactions.
- Personality is the first authored branch. Exact location, region, guild, reputation, race/culture, quest state, survival, combat, and interior type remain reactive beneath that worldview. Action Movie one-liners intentionally remain outside the personality tree.
- Adds lore-domain writing across the archetypes plus major Iliac Bay locations, guilds, reputation states, race/cultural seasoning, semantic choices, and spoiler-gated Main Quest hooks.
- Adds tavern, temple, guild hall, library, palace, bank, shop, and generic-building entry contexts.
- Adds optional subtitles for automatic/manual barks and typed speech: **Off**, **Text only**, or **Player name + text**. Speaker names are visual only and are never sent to Kokoro.
- Keeps the compact single-line keyboard speech input and location/guild/reputation context from v0.1.5.
- `Barks.json` advances to **schema v4** and merges shipped additions into existing user libraries without requiring deletion.
- Enlarges/lowers the composed player head for the shared NPC conversation presentation.

See `PERSONALITY-GRIMOIRE.md` for the archetype map and `PLAYER-WRITING-BIBLE.md` for writing rules.

## v0.1.4.1 Ask interop + writing pass

- Registers player head/name/race/personality/location metadata with NPC v0.1.13. The NPC module can use that identity to augment the **existing** normal Talk/Ask window rather than replacing it.
- Ask questions remain the exact host text. Race/personality never rewrites Where Is / Tell Me About / ordinary Ask questions.
- Expanded `Barks.json` schema v3 with a Daggerfall-era writing pass guided by Morrowind's concise, culturally grounded dialogue discipline. Race now affects more combat, travel, dungeon, survival, and quest contexts; personality overlays also cover more than general idle barks.
- Modern quip language stays isolated to optional `Action.*` pools.
- Existing `Barks.json` files migrate in place: custom lines are retained, new shipped lines/keys are merged, and the retired normal-mode phrase `running on fumes` is removed.
- See `PLAYER-WRITING-BIBLE.md` for the content rules.

## v0.1.3 framework UI + voice-selection pass

- Renamed the module to **Daggerfall Narrator - Player** while retaining its existing GUID, namespace, and persistent-data paths for upgrade compatibility.
- Normal DFU **Ask / Where Is / Tell Me About** questions can now be voiced through a soft bridge from the NPC module. The exact vanilla question is spoken and NPC speech waits for the player dialogue turn instead of colliding with it.
- Native voice selection no longer asks for a Kokoro voice ID. Choose **US/UK**, **Male/Female**, and a broad **Neutral/Warm/Refined/Energetic/Rugged** delivery; PlayerVO resolves that combination to a curated installed Kokoro voice.
- Adds **Voice Depth** and **Speed** controls. Depth applies a modest pitch shift without changing the selected base voice.
- The options menu is organized into General, Voice, Personality, Barks, Integrations, and advanced Cache sections.
- Choose a **Test Sample** in Voice settings. Applying changes to accent, gender, delivery, depth, speed, or audio style replays that sample so the configured voice can be auditioned without entering a raw voice ID.


## v0.1.2 test-feedback fixes

- Sound Test settings use DFU's documented live-settings registration path.
- Bark-key input is hardened and Hybrid mode uses tap-to-bark / hold-to-type with raw-state fallback.
- NPCVO-enhanced quest conversations retain the player portrait and last player response for continuity after the spoken line ends.
- The player portrait now uses dedicated head/face art. The default keeps the race paperdoll scene only as a backdrop, avoiding the heavily pixelated upper-body paperdoll crop.

## Conversational player portrait

When NPCVO enhanced quest presentation is available, **Player Portrait Style** controls the texture PlayerVO sends to NPCVO:

- **Head + Paperdoll Background** (default): uses the dedicated player head/face art over the native race paperdoll scene. This keeps the DFU visual language without shrinking the low-resolution equipment paperdoll into the dialogue frame.
- **Head + Dark Backdrop**: uses the same head art over a simple dark portrait field.
- **Head Only**: sends the head art without a scene backdrop.

PlayerVO caches this composed portrait and registers it with NPCVO as a persistent conversation identity. **NPCVO v0.1.11 or later is recommended** for the continuity behavior where the player portrait and last response remain visible between turns. Older NPCVO builds can still receive the player-turn texture, but revert to one-sided presentation after speech ends.

## Core features

- Choose a player voice by accent, gender, delivery, depth, and speed, then audition it from Mod Settings.
- Automatic contextual barks for combat, world transitions, health/vitals, quests, bows, magic, and supported survival context.
- Configurable **Bark key**: context bark, keyboard speech, or hybrid tap-to-bark / hold-to-type.
- Keyboard speech reads exactly what the player typed. Race/personality logic never rewrites player-authored text.
- Race/culture-flavored authored lines beneath the Birthsign personalities. The Apprentice maps to the Agent: a newcomer to the land who learns quietly, stays focused on the larger quest, and uses few words.
- Optional **Action Movie** mode with context-matched movie-reference one-liners and lore-friendly variants.
- Vanilla player vocalizations have priority. Damage, exertion/gasping, and death interrupt or delay PlayerVO instead of stacking voices.
- Health/vitals context includes low/critical health, recovery, fatigue, magicka, breath, and encumbrance threshold changes.
- Reflection-only **Climates & Calories** integration for hunger, starvation, thirst, wetness, temperature, camping, and cooking when that mod is present.
- Generic quest-structure awareness for accepted/resolved quests plus named quest bark keys. PlayerVO does not pretend to understand arbitrary quest prose.
- Safe authored responses for recognized Yes/No/Accept/Reject and court intents while preserving the original DFU callbacks.
- Optional **NPCVO bridge**: when NPCVO is present and its enhanced quest UI is active, PlayerVO adds the player portrait and a typewritten/spoken player turn. When NPCVO is absent, vanilla DFU UI is untouched.
- Editable persistent `Barks.json` generated on first run.
- Rolling speech cache and console test commands.

## Action Movie mode

This is an optional authored-reference layer, not a random quote machine. References are only eligible when their event fits. Examples include bow-draw, combat victory, heavy damage, magic, and exact integration events such as `FrostKill` and `FireKill`.

The v0.1.0 foundation automatically detects general magic casting. Exact elemental **kill classification** is exposed through `PlayerVOMod.NotifyContext("FrostKill")` / `NotifyContext("FireKill")` for future combat integrations rather than guessing the killing effect.

## Manual Bark / keyboard speech

Default key: `V`.

Default mode: **Hybrid**.

- Tap: contextual authored bark.
- Hold: opens a small DFU input box and speaks the exact submitted text.
- Automatic barks have a long global cooldown and category cooldowns.
- Manual barks have a short anti-spam cooldown.
- Typed speech has only a very short submit lockout.

## NPCVO conversation integration

PlayerVO soft-detects `NPCVO.NPCVOMod` through reflection. There is no compile-time dependency.

For ordinary DFU TalkWindow questions, NPCVO can hand the exact selected player question to PlayerVO. PlayerVO speaks it as a high-priority dialogue turn, and NPCVO defers the corresponding answer until the player line is finished. If either module is disabled, the normal DFU conversation UI continues unchanged.

When the NPCVO quest presentation is available, a recognized semantic player choice can be presented as:

1. the native DFU choice callback fires normally;
2. PlayerVO selects an authored response for the selected birthsign personality and optional race/cultural flavor;
3. NPCVO displays the player face/name and PlayerVO reveals the response as it is spoken;
4. the player portrait and completed response remain visible while NPCVO continues with the next NPC turn.

The actual quest choice remains the original DFU choice. PlayerVO never changes Yes into No or replaces quest callbacks.

## Optional integration API

Other mods can submit precise context without linking PlayerVO at compile time:

```csharp
PlayerVO.PlayerVOMod.NotifyContext("FrostKill");
PlayerVO.PlayerVOMod.NotifyContext("FireKill");
```

This is useful when another mod already knows an exact event better than PlayerVO can infer it.

## Console commands

- `playervo_test` — configured player voice test.
- `playervo_bark` — force a contextual bark.
- `playervo_say <text>` — speak exact text.
- `playervo_event <eventKey>` — inject/test a context event.
- `playervo_reload` — reload settings and `Barks.json`.
- `playervo_status` — print current runtime status.
- `playervo_clear_cache` — clear generated speech cache.

## Files created on first run

Under `<DFU persistent data>/PlayerVO/`:

- `PlayerVO.ini`
- `Barks.json`
- `Cache/`

## Build

This package is **build-ready source**, not a precompiled `.dfmod`.

1. Use the Daggerfall Unity 1.1.1 source project with Unity 2019.4.40f1.
2. Copy `Assets/Game/Mods/PlayerVO/` into the same path in the DFU source project.
3. Confirm Unity compiles with zero red errors.
4. Open the DFU Mod Builder and build `PlayerVO.dfmod` with the normal precompiled workflow.
5. Install the resulting `.dfmod` under `DaggerfallUnity_Data/StreamingAssets/Mods/`.
6. Run the shared Kokoro service before testing.

## v0.3.0 - Daggerfall Voice Engine + Birthsign character creation
Player speech now uses **Daggerfall Voice Engine** for cross-module turn arbitration. Voice and personality are separate. DFU biography Q&A can suggest one of the 13 Birthsign-inspired archetypes, followed by a manual character-creation picker; the choice is stored per character. The Apprentice is the Agent: new to the land, quiet, mission-focused, and sparing with words. Ambient chatter stays sparse, while deliberate dialogue and key quest/danger reactions remain available.


## v0.3.2 - Character creation reliability
PlayerVO now runs its character-creation UI before a PlayerEntity exists and uses CreateCharSummary as the definitive one-time personality picker point. Biography inference is retained when available. The dedicated picker is the only character-creation personality UI, avoiding overlap with DFU's crowded summary sheet.


## v0.3.3 - Personality-aware observations
When Dungeon Master routes a look/observation to PlayerVO, the protagonist now converts vanilla second-person text to first-person and adds a restrained reaction based on the selected Birthsign personality. The observed subject remains intact, so `You see a rat.` becomes a personality-appropriate first-person line rather than being spoken verbatim. The Apprentice/Agent declines automatic routed observations, allowing Dungeon Master to narrate them instead.


## v0.3.5 - Release candidate polish

- Saves player voice selection, exact voice override, depth, speed, and audio-quality style per character.
- Adds an optional exact Daggerfall Voice Engine `VoiceOverride`; leave it blank to use the normal accent/gender/delivery controls.
- US male Rugged now uses the bundled `am_granite` voice.
- Removes the non-functional Mod Settings voice-audition selector. Console diagnostics remain available.
- Reworks the Birthsign preview art into hand-authored constellation patterns rather than literal geometric icons. Hovering the left list now drives both the preview and highlight state.
- Rewrites Birthsign biographies as vague backstory/temperament clues only. They never imply mechanical powers, bonuses, or changes to the game's actual birthsign.
- The Apprentice archetype is now **The Agent**: newly arrived in the land, a quiet learner dedicated to the larger quest and sparing with words.
- Quest-giver Yes/No and Accept/Reject speech uses personality-authored responses while preserving the original DFU quest callback.
- Removes the personality overlay from the Create Character Summary sheet to eliminate clipping across skills/reflex UI.
- Bumps the Player cache schema so legacy-quality changes cannot accidentally reuse stale WAVs.
