# Daggerfall Narrator - NPC v0.2.4 - Daggerfall Voice Engine module


## v0.1.15 conversation continuity hotfix

- Quest-offer choices are visible again. NPC mirrors the native DFU choice buttons using their current textures/hotkeys and forwards clicks to the original controls, so the quest callbacks remain DFU-owned.
- Quest giver and player names are anchored beneath their own portraits.
- During normal Ask dialogue, the next NPC answer stays hidden while Player voices the selected question and reveals only when NPC takes the turn.
- Ask identity labels are now conservative: always show the NPC name, then only a meaningful title or known service role. Broad classifier labels such as `CommonerBreton` are suppressed.

## v0.1.13.1 shared Ask-layout interop

- When **Daggerfall Narrator - Player** is present, normal Ask / Where Is / Tell Me About windows can add a persistent player head and compact player/NPC identity lines while keeping the host TalkWindow itself alive.
- The same geometry is used for vanilla, Advanced Dialogue, and texture/font replacements. The module does **not** recreate topic lists, Tone, Previous List, scrollbars, Copy to Logbook, or callbacks.
- The existing player-question label is narrowed beside the player portrait, so the question remains the host UI's own text. Player speaks that exact question, then NPC speaks the answer.
- **Player Ask Conversation UI** can disable the visual augmentation independently. If Player is absent, normal Talk UI is untouched.

## v0.1.12 Ask/Talk bridge + settings cleanup

- Renamed the module to **Daggerfall Narrator - NPC** while keeping the existing GUID, namespace, data folders, VoiceTypes, and VoicePack paths for upgrade compatibility.
- Ordinary DFU TalkWindow player questions now soft-detect **Daggerfall Narrator - Player**. When available, the exact vanilla Ask / Where Is / Tell Me About question is sent to the Player module for speech.
- The next NPC answer is held in NPCVO's speech pipeline while the player dialogue turn is active, preventing both modules from synthesizing/speaking the exchange at once. A fail-safe timeout returns control to NPCVO if the Player bridge disappears.
- If the Player module is missing or disabled, NPCVO behaves exactly as before and speaks only the NPC side.
- Mod Settings are consolidated into General, NPC Voices, Speech Style, Portraits & Integration, Audio, and Advanced sections.


## v0.1.11 test-feedback fixes

- Sound Test settings now follow DFU's documented live-settings callback registration flow.
- PlayerVO can register its player portrait/name before a spoken turn begins. NPCVO keeps that identity visible throughout the enhanced quest conversation.
- The last player response remains on-screen after speech ends while NPCVO proceeds to the next NPC line, preserving a two-sided conversational layout.

## v0.1.10 framework + Redguard/Orc tuning

- Redguard male VoiceTypes now use an Orc-inspired baritone processing curve while retaining the ordinary human voice pool.
- Male Orcs sit slightly lower/deeper than v0.1.9 to keep the two archetypes distinct.
- Built-in VoiceTypes schema is v6.
- NPCVO exposes a soft PlayerVO conversation-turn bridge so PlayerVO can add the player portrait/typewritten reply to NPCVO quest presentation without becoming a hard dependency.


Companion NPC voice mod for **Daggerfall Unity 1.1.1**. NPCVO uses **Daggerfall Voice Engine** for local Kokoro/ONNX synthesis and is designed to coexist with **Dynamic Portraits (Nexus mod 1178)**.

## v0.1.9 feature-completion pass

- **Persistent procedural NPC voices:** NPCVO now writes `VoiceAssignments.json`. Once a procedural NPC receives a Kokoro voice, that exact base voice and its variation units remain attached to a stable game identity across conversations, save/load cycles, restarts, social-role reclassification, and portrait changes. The assignment deliberately outlives later role/profile reclassification; use `npcvo_clear_assignments` only when you intentionally want procedural NPCs recast.
- **Speech Source setting:** choose **Hybrid** (pre-generated WAV first, Kokoro fallback), **Kokoro only**, or **Pre-generated WAV only**. Pre-generated-only mode preserves vanilla line parity by simply leaving the native text silent when an authored WAV does not exist.
- **Merchant/service menus stay silent.** NPCVO does not treat Trade/Shop/Store/Merchant service windows as conversations, preventing repetitive menu narration. Ordinary spoken dialogue from a merchant NPC still works when it occurs in the normal TalkWindow.
- **Actual legal-court narration:** DFU's court/judge message boxes are detected from the `DaggerfallCourtWindow` ownership chain and voiced through a dedicated dramatic **Judge** VoiceType: slower, lower, formal and resonant. Guilty/Not Guilty/Debate/Lie buttons remain silent.
- **In-settings Sound Test:** the mod settings now expose race/gender, Daedra, and Judge audition presets plus social-role selection. Changing the preset plays the same Kokoro test path used by the console audition commands.
- **Cache v2:** runtime Kokoro cache keys now include the complete DSP stack so changing creature filters, reverb, throat/croak/purr/growl processing, or audio style cannot accidentally reuse an old render.
- Kokoro is the only runtime synthesizer. Pre-generated WAVs are engine-agnostic authored assets; NPCVO does not require or document a second TTS runtime.


## v0.1.8.3 heroic Nord + Granite-only community voice

- Male Nords now use a dedicated **Heroic Nord** pool anchored by the community `am_granite` voice instead of the generic male pool. The default blends weight Granite at roughly two-thirds with a British Kokoro male for a restrained old-world color while keeping English G2P stable.
- Heroic Nord delivery is slower, lower, more compressed and more resonant, with only light gravel. Social role still changes the US/UK balance, so nobles lean more British while commoners remain Granite-dominant.
- Historical note: this release originally used SharedKokoro v1.5.1. Current v0.2.0 releases instead use Daggerfall Voice Engine, which bundles `am_granite` in the self-contained engine package.
- `am_granite` behaves like a normal voice ID in NPCVO profiles. Kokoro comma-separated blending is supported, including weighted blends by repeating an ID (for example `am_granite,am_granite,bm_george`).
- If Granite cannot be downloaded, the shared server falls back to `am_fenrir` rather than breaking NPC dialogue.
- `VoiceTypes.json` built-ins are now schema v3 so installed v0.1.8 profiles refresh to the heroic Nord defaults while custom extra profile keys are retained.
- See `COMMUNITY-VOICES.md` for the integrated pack and other community projects evaluated for future expansion.


## v0.1.8 creature voice pass

- Male Orcs now use curated low-register Kokoro pools and a deeper, slower, disciplined baritone profile rather than generic beast growl.
- Male Dunmer now use curated low-register voices, substantially deeper pitch, and much stronger dry gravel. Female Dunmer use a deeper, slower, smoother profile with a curated lower female pool.
- Argonians gain a dedicated reptilian throat stack: subharmonic body, emphasized sibilance, short throat-cavity resonance, micro-flutter, and croak processing.
- Khajiit gain feline chest processing: subtle purr modulation, throat resonance, gentle growl, and breath/air layers. Purr/growl intensity reacts to NPCVO emotion.
- Generic Daedra spectral processing is stronger, with a clearly audible void/chamber echo tail on Default. Named Daedra keep their individual profiles on top of the improved shared reverb algorithm.
- Historical note: these creature-DSP parameters originated in the Python backend. Daggerfall Voice Engine v1.0 preserves the same request fields in the ONNX backend.
- `VoiceTypes.json` / `SpecialVoices.json` built-in profiles use schema v2. An older built-in profile file is backed up once and refreshed so the new defaults actually take effect; non-built-in custom keys are retained.

## v0.1.7 delivery, emotion, and special-character layer

- Optional **Emotion Layer** adds Friendly, Formal, Angry, Afraid, Sad, Excited, Threatening, and Injured delivery presets by adjusting cadence/pitch and NPCVO DSP after Kokoro synthesis parameters are resolved.
- When **Dynamic Portraits Emotion Sync** is enabled, NPCVO reads Dynamic Portraits' active Win/Loss reaction and Happy/Angry reputation mood through reflection and prefers that signal for normal TalkWindow speech. The integration is read-only: NPCVO does not force Dynamic Portraits into an expression state.
- Optional **Grammar Polish** cleans punctuation/spacing, sentence capitalization, and a deliberately tiny known-typo list before synthesis. The untouched DFU string is still used for game logic and VoicePack hashing. **Display Polished Text** can be disabled independently.
- Adds `SpecialVoices.json`: curated Kokoro fallbacks for major story characters and all Daggerfall Daedric Princes. User `UniqueVoices.json` still overrides these.
- Daedric Princes and other supernatural curated profiles can use `spectral` and `reverb` DSP fields. v0.1.7 introduced these in shared Kokoro v1.3; the current v0.1.10 package ships shared Kokoro v1.5.1.
- Exact authored special-character WAVs now have a canonical path: `VoicePack/Characters/<slug>/Lines/<hash>.wav`. Existing legacy VoicePack folders still work.
- See `SPECIAL-NPC-VOICE-RESEARCH.md` and `VoicePack/Characters/manifest.json` for the researched starter cast and authoring structure.

### v0.1.7 voice priority

1. Exact pre-generated VoicePack WAV
2. User named NPC override (`UniqueVoices.json`)
3. Curated story/Daedra fallback (`SpecialVoices.json`)
4. Portrait override (`PortraitVoices.json`)
5. Race + gender + social VoiceType
6. Race + gender fallback
7. Gender + social fallback
8. Gender fallback
9. Generic fallback

## v0.1.6 procedural voice overhaul

- Uses all **28 supported English Kokoro voices** across US and UK pools.
- Procedural NPC VoiceTypes now resolve primarily by **race + gender + social role**. Existing custom exact profiles still override generated defaults.
- Social class drives accent weighting: nobles/scholars favor UK voices, commoners/underworld favor US voices, and Mer receive an additional UK bias.
- Humans and Mer keep restrained pitch/speed variation. Orcs, Khajiit, Argonians, and Dragons receive stronger optional fantasy DSP.
- Male Dark Elves receive a dedicated dry/gravelly parallel filter inspired by Morrowind-era Dunmer voices.
- Children remain moderately higher/faster without an exaggerated helium effect.
- Pre-generated unique-NPC VoicePack WAVs now bypass Kokoro resolution entirely when present; missing lines fall back through Unique/Portrait/VoiceType resolution.
- The shared Kokoro server adds `/voices/install-english` and can download/verify every English voice through Kokoro's normal `load_voice()` cache path.
- New console commands: `npcvo_voice_install` and `npcvo_voice_status`.
- New **Fantasy Processing** setting: Subtle / Default / Strong.

### Procedural accent model

Base UK weighting by social role is approximately: Noble 85%, Scholar 68%, Guild 55%, Merchant 40%, Commoner 18%, Underworld 12%. High/Dark/Wood Elves add an additional UK bias. The chosen voice, pitch, speed, and DSP remain deterministic for a given NPC identity.

### Voice priority

1. Exact pre-generated VoicePack WAV
2. Named unique-NPC Kokoro override
3. Curated story/Daedra Kokoro fallback
4. Portrait override
4. Race + gender + social VoiceType
5. Race + gender fallback
6. Gender + social fallback
7. Gender fallback
8. Generic fallback

## v0.1.5.2 modal quest diagnostics

DFU quest prompt message boxes are modal, so the developer console cannot be opened while they are visible. NPCVO now automatically snapshots relevant message-box state while the prompt is open and writes changes to `Player.log` with the prefix `[NPCVO][QuestDebug]`. After closing the prompt, run `npcvo_quest_debug` to print the last captured snapshot. The diagnostic includes Yes/No detection, callback delegate targets, questor state, extracted text length, and the last clicked NPC.


## v0.1.5.1 typewriter presentation

Normal NPC answers and quest-giver dialogue now use a true typewriter presentation: unreached characters are invisible and characters appear progressively with speech in the normal dialogue color. The underlying full TalkWindow line remains intact for DFU logic, and skipping/interruption restores the full line immediately.

## v0.1.5 quest-script prompt fix

This build adds direct detection for DFU quest-script `prompt` message boxes. Procedural QuestorOffer prompts are plain `DaggerfallMessageBox` instances created by `QuestMachine.CreateMessagePrompt()`, not necessarily `UIWindowType.QuestOffer`. NPCVO now recognizes the quest `Prompt` action's button callback, binds the last clicked questor as speaker, and can attach narration/portrait UI directly to that message box.

This build repairs the VoiceTypes initialization failure that could leave `VoiceTypes.json` as `[]`, causing all NPC voice resolution and `npcvo_test` to fail. Existing `[]`, `{}`, blank, or incomplete VoiceTypes files are repaired automatically at startup. `npcvo_test` also has a direct `bm_george` fallback so it can independently test Kokoro.

## What v0.1.5 includes

### Quest-giver portrait dialogue UI

Quest offers no longer have to look like disconnected anonymous message boxes. When **Quest Giver Portrait UI** is enabled, NPCVO decorates the QuestOffer speaker area with:

- the quest giver's 64x64 face using DFU's Talk/Ask portrait-resolution rules
- the quest giver's generated/display name
- the quest paragraph in a dedicated dialogue area
- the same speech-synced progressive text reveal used for ordinary NPC answers

NPCVO does **not** replace the QuestOffer window class or recreate its quest logic. The original DFU message box, Accept/Reject/Yes/No buttons, button events, and quest callbacks remain alive. NPCVO hides only the original speaker text and draws its presentation layer over that region.

This is deliberate for compatibility: accepting/refusing a quest should still be handled by DFU or by a modded QuestOffer subclass exactly as before.

If the common `DaggerfallMessageBox` QuestOffer presentation is not used, NPCVO has a fallback path for direct/custom QuestOffer windows. It hides only paragraph-like quest text and skips button subtrees.

### Portrait resolution

For quest givers, NPCVO resolves the face from the questor's faction and billboard data using the same rules DFU's normal TalkManager uses for the Ask/Talk portrait. It selects either:

- `TFAC00I0.RCI` for common faces
- `FACES.CIF` for special/story faces

Because NPCVO now reuses the TalkWindow portrait texture directly, DFU (and portrait mods) remain responsible for choosing the actual face.

NPCVO first checks DFU's standard CIF/RCI texture-replacement path, then falls back to the original Arena2 art.

### Dynamic Portraits compatibility seam

Current Dynamic Portraits releases are centered on the normal `DaggerfallTalkWindow`, so v0.1.5 does **not** claim to animate quest-offer portraits through Dynamic Portraits yet. The quest UI reuses the 64x64 portrait texture already resolved on DFU's TalkWindow.

To make a future bridge straightforward, NPCVO exposes three public reflection-friendly properties while a quest portrait is active:

- `NPCVO.NPCVOMod.ActiveQuestPortraitPanel`
- `NPCVO.NPCVOMod.ActiveQuestPortraitCif`
- `NPCVO.NPCVOMod.ActiveQuestPortraitRecordId`

A future Dynamic Portraits compatibility update can therefore animate or replace the exact panel NPCVO is already using without replacing NPCVO's quest UI or DFU's quest logic.

## Runtime architecture

NPCVO has one optional runtime synthesizer: **Kokoro**. The **Speech Source** setting controls whether NPCVO uses Kokoro, exact authored WAVs, or both.

Hybrid voice priority:

1. Exact pre-generated VoicePack WAV
2. User named NPC Kokoro override (`UniqueVoices.json`)
3. Curated story/Daedra Kokoro fallback (`SpecialVoices.json`)
4. Dynamic Portraits portrait profile (`PortraitVoices.json`)
5. Race + gender + social-role Kokoro VoiceType
6. Race + gender fallback
7. Gender + social-role fallback (including child portraits)
8. Gender fallback
9. Generic Kokoro fallback

**Kokoro only** skips step 1. **Pre-generated WAV only** skips steps 2-9 and leaves the normal DFU text presentation intact when no authored line exists. Pre-generated WAVs can be created by any offline audio workflow; NPCVO only cares about the final WAV and matching hash/path.

Procedural Kokoro assignments are persisted in `VoiceAssignments.json`. The persistence key is deliberately independent of Dynamic Portraits' current portrait and NPCVO's inferred social role, so presentation/classification changes do not casually recast an NPC.

## Dynamic Portraits pairing for normal dialogue

Dynamic Portraits remains an optional/soft dependency. NPCVO has no compile-time reference to its assembly and does not Harmony-patch its methods.

When Dynamic Portraits is active in a normal TalkWindow, NPCVO can read its portrait identity through reflection and convert it to a stable key such as:

`TFAC00I0.RCI_10`

That key can be assigned a voice in `PortraitVoices.json`, allowing portrait packs and voice profiles to stay paired without NPCVO needing to own the portrait artwork.

NPCVO can also detect Dynamic Portraits' portrait AudioSource. The default behavior is **Defer NPCVO while portrait audio plays**, preventing short portrait greeting/reaction WAVs from talking over full dialogue.

## How NPC dialogue is captured

### Normal TalkWindow

NPCVO watches the active TalkWindow conversation list. Player questions are excluded; NPC greetings and responses are voiced.

v0.1.1+ tracks conversation-row object identity **and** source text rather than assuming every response only appears at a new numeric index. This supports vanilla appended responses and alternate dialogue UIs that recycle rows.

### QuestOffer windows

v0.1.2+ separately watches DFU's QuestOffer path, including cases where the QuestOffer window displays its text through a child `DaggerfallMessageBox`.

The visible NPC paragraph must remain stable across two polling passes before speech starts. Accept/Reject/Yes/No button captions are excluded from speech.

v0.1.5 additionally presents that captured text in the portrait dialogue region. NPCVO deliberately excludes its own custom panel from later text scans, so speech-synced partial text cannot be mistaken for a new quest line. The hidden original DFU text remains available for detecting follow-up offer/accept/refuse paragraphs.

### Legal court/judge windows

v0.1.9 watches legal-court `DaggerfallMessageBox` windows owned by DFU's court UI and voices the visible judicial text with a stable dramatic Judge VoiceType. The normal court choice buttons remain native and silent. This is intentionally separate from merchant/service windows, which NPCVO excludes from dialogue capture.

The setting **Narrate Legal Court Judge** can disable this court narration independently. Court narration follows the same Speech Source rule as other NPCVO speech, so a Pre-generated WAV only setup can author court lines without forcing a Kokoro fallback.

## Progressive dialogue text

`Typewriter NPC text with speech` is enabled by default.

For ordinary TalkWindow answers, NPCVO keeps DFU's full original answer intact, makes only its native glyphs transparent, and draws a second label that reveals with the WAV.

For v0.1.5 quest portrait dialogue, the custom dialogue label begins blank and reveals with the same playback clock.

Kokoro currently returns WAV audio without word timestamps, so reveal timing follows the clip's real playback position using a conservative character clock. Full text is restored when playback completes, fails, is interrupted, or its UI closes.

Player questions and quest-choice buttons are never progressively hidden by NPCVO.

## Mod Settings relevant to special dialogue

- **Narrate Quest Giver Windows**: voices text from the separate QuestOffer UI.
- **Narrate Legal Court Judge**: voices judicial text from DFU's legal court UI with the dramatic Judge profile while leaving court-choice buttons silent.
- **Quest Giver Portrait UI**: adds the portrait/name/dialogue presentation while retaining native quest controls.
- **Typewriter NPC Text With Speech**: reveals normal NPC answers and quest dialogue character-by-character while speech plays, using the normal dialogue color rather than the selected/highlight palette.

Turning **Quest Giver Portrait UI** off restores the vanilla visual QuestOffer presentation; QuestOffer narration can remain enabled independently.

## Shared Kokoro

NPCVO talks to:

`http://127.0.0.1:5000/synthesize`

The shared server accepts the existing Narrator fields plus:

`pitch_semitones`

Narrator does not need to send this field; it defaults to zero. NPCVO uses small deterministic pitch/speed variation so the same Kokoro base voice can represent multiple procedural NPCs while a given NPC remains stable.

Recommended default variation:

- Subtle: approximately ±0.65 semitone and ±4% speed
- Moderate: approximately ±1.25 semitones and ±6% speed

## Files created on first launch

NPCVO creates these under its persistent-data `NPCVO` folder:

- `NPCVO.ini` - advanced backend options such as Kokoro port
- `VoiceTypes.json` - foundational race/gender VoiceTypes
- `UniqueVoices.json` - user exact named NPC overrides
- `SpecialVoices.json` - built-in/persistent curated story and Daedra fallbacks
- `PortraitVoices.json` - exact portrait-key overrides
- `VoiceAssignments.json` - automatically maintained persistent procedural NPC-to-voice assignments; normally do not edit this by hand
- `Cache/` - disposable runtime Kokoro WAVs
- `VoicePack/` - permanent local pre-generated WAV overrides

Edit the authoring JSON files, then use `npcvo_reload` in the DFU console. `VoiceAssignments.json` is runtime state and is maintained automatically.

## Pre-generated unique NPC packs

Installable pack path:

`DaggerfallUnity_Data/StreamingAssets/Sound/NPCVO/VoicePack/<NPC_NAME>/<LINE_HASH>.wav`

Use `npcvo_lineinfo` immediately after a dialogue line to print the exact expected hash/path.

The default `Ignore player name in VoicePack keys` option removes the player's name from the matching key. This allows a prerecorded unique-NPC line to omit the player's name while the visible DFU dialogue remains unchanged.

`VoicePackTools/PrepareVoicePackLine.py` is included as an offline helper for placing an authored WAV into the correct hash layout.

## Console commands

- `npcvo_status` - backend, Dynamic Portraits detection, progressive-text and quest-portrait status
- `npcvo_who` - current normal-Talk NPC identity, portrait key and resolved voice
- `npcvo_test` - synthesize a basic backend test line through shared Kokoro
- `npcvo_test_voice <race> <gender> [role]` - audition any procedural VoiceType directly (for example `npcvo_test_voice DarkElf Male Noble`)
- Race/gender shortcuts: `npcvo_test_breton_m/f`, `npcvo_test_redguard_m/f`, `npcvo_test_nord_m/f`, `npcvo_test_darkelf_m/f`, `npcvo_test_highelf_m/f`, `npcvo_test_woodelf_m/f`, `npcvo_test_khajiit_m/f`, `npcvo_test_argonian_m/f`, `npcvo_test_orc_m/f`, and `npcvo_test_dragon_m/f`
- `npcvo_test_daedra` - audition the generic spectral Daedra fallback without using a named Daedric Prince profile
- `npcvo_stop` - stop current NPC speech
- `npcvo_reload` - reload INI and voice JSON files
- `npcvo_lineinfo` - show the last line's VoicePack normalized key/hash/path
- `npcvo_clear_cache` - clear generated Kokoro WAVs only
- `npcvo_clear_assignments` - clear the persistent procedural casting table so NPCs are recast on their next voice resolution

## Building the .dfmod

This archive is source/build-ready, not a compiled `.dfmod`.

1. Use the **Daggerfall Unity 1.1.1 source project** in **Unity 2019.4.40f1**.
2. Copy `Assets/Game/Mods/NPCVO` into the same path in the DFU source project.
3. Let Unity compile and make sure the Console has no red errors.
4. Open **Daggerfall Tools > Mod Builder**.
5. Select `NPCVO` and use the same Precompiled workflow as your Narrator build if desired.
6. Build `NPCVO.dfmod`.
7. Install it to `DaggerfallUnity_Data/StreamingAssets/Mods/`.
8. Run the shared `StartKokoro.cmd` before testing.

## Recommended v0.1.9 test

1. Run a **Sound Test** preset in Mods > NPCVO > Settings, then confirm `npcvo_test_voice` still works from the console.
2. Talk to one procedural NPC, note `npcvo_who`, close/reopen dialogue, save/load, restart the game, and verify the NPC keeps the exact same assigned base voice. If Dynamic Portraits is installed, also verify a portrait change does not recast that NPC.
3. Open a merchant Trade/Shop/Store service menu and confirm it remains silent. Return to the merchant's ordinary TalkWindow and confirm normal spoken dialogue still works.
4. Trigger DFU's legal court after a crime and confirm the court narrative/sentence is voiced with the slower/formal Judge profile while Guilty/Not Guilty/Debate/Lie buttons remain silent.
5. Test **Hybrid**, **Kokoro only**, and **Pre-generated WAV only**. In pre-generated-only mode, a line without a matching WAV must remain readable and silent rather than blocking dialogue.
6. Re-test normal NPC greeting/answers and quest-giver paragraphs, including typewriter reveal and interruption/close behavior. Player questions and Accept/Refuse/Yes/No controls must stay silent.
7. Change a fantasy-DSP value or profile, replay a line, and confirm NPCVO produces/uses a new runtime cache entry rather than a stale render.
8. Stop the Kokoro server while in Hybrid/Kokoro mode and confirm failed synthesis restores the full text and never blocks the conversation.

Useful `Player.log` sequence:

- `[NPCVO] Captured quest-giver line ...`
- `[NPCVO] Captured legal-court line ...`
- `[NPCVO] Quest portrait UI attached ...`
- `[NPCVO] Kokoro synth request ...`
- `[NPCVO] Ready to play runtime WAV ...`
- `[NPCVO] Playback started ...`

## Current limitation

The quest portrait is static. Dynamic Portraits can still operate normally in TalkWindow, and NPCVO now exposes the active quest portrait panel/texture for a future animated integration. That bridge should be implemented only after the basic quest UI is confirmed not to interfere with accepting/refusing quests.


## v0.1.5 QuestOffer detection fix

Quest offers are now detected through the DaggerfallMessageBox `PreviousWindow` ownership chain before falling back to the UI stack. Quest-giver identity uses QuestMachine.LastNPCClicked when available, and the portrait uses DFU's own billboard-to-face resolver. Use `npcvo_quest_debug` while a quest offer is visible if detection still fails.


## v0.1.5.3 quest-offer owner fix

Procedural quest parchment message boxes can link back to `DaggerfallQuestOfferWindow` only through message-box delegate targets (for example `QuestPopupMessage_OnClose`). v0.1.5.3 recovers that owner directly, enabling quest voice, portrait UI, and speech-synced typewriter reveal on this path.


## v0.1.5.4 true typewriter fix

TalkWindow narration now replaces the ListBox's native drawing label with a no-draw proxy while speech is active. The proxy still contains the complete original text and matching wrap metrics for DFU bookkeeping, but only NPCVO's progressively revealed overlay is rendered. This prevents the full line from remaining visible underneath and appearing to be highlighted.

## v0.2.0 - Daggerfall Voice Engine
NPC speech now participates in the shared **Daggerfall Voice Engine** turn queue. Existing pre-generated WAV behavior remains available. NPC/quest dialogue is high priority, player-question sequencing is preserved, and the engine receives conservative delivery/emotion hints without changing vanilla dialogue text.


## v0.2.2 - Conversation layout finishing pass
Quest dialogue now gives the player portrait/name a deliberate right-side inset, while normal TalkWindow questions anchor the player identity beside the response/Tone area. NPCVO also exposes a brief post-quest ownership signal and can claim one immediate plain quest Message: continuation so Dungeon Master does not narrate NPC speech.


## v0.2.4 - Stability + clean Q&A

- Normal Ask/Tell Me About keeps the host/vanilla conversation UI visually untouched. PlayerVO can still voice the selected player question, but NPCVO no longer injects a player portrait/name beside the Tone panel.
- Speakerless quest-script prompts (including tutorial/system prompts) are no longer treated as NPC conversations unless NPCVO can establish a credible clicked NPC/questor. This prevents blank `Unknown NPC` quest windows.
- Every NPC speech interruption path now cancels the active Daggerfall Voice Engine turn before stopping the coroutine.
- PlayerEntity/save transitions hard-reset NPC dialogue UI, deferred questions, external player identity, speech state, and all NPC-owned Voice Engine turns.
- Enhanced quest-giver parchment presentation remains intact for real NPC quest prompts.
