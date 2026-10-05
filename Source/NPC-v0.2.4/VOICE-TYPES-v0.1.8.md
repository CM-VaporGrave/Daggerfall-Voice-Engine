# NPCVO v0.1.8 VoiceTypes

NPCVO resolves spoken NPC lines in this order:

1. Exact pre-generated `VoicePack` WAV
2. `UniqueVoices.json` named-NPC override
3. `PortraitVoices.json` portrait override
4. Race + gender + social role
5. Race + gender
6. Gender + social role
7. Gender
8. Generic

## English Kokoro catalog

The built-in procedural framework uses all 28 supported English IDs:

- US female: `af_alloy`, `af_aoede`, `af_bella`, `af_heart`, `af_jessica`, `af_kore`, `af_nicole`, `af_nova`, `af_river`, `af_sarah`, `af_sky`
- US male: `am_adam`, `am_echo`, `am_eric`, `am_fenrir`, `am_liam`, `am_michael`, `am_onyx`, `am_puck`, `am_santa`
- UK female: `bf_alice`, `bf_emma`, `bf_isabella`, `bf_lily`
- UK male: `bm_daniel`, `bm_fable`, `bm_george`, `bm_lewis`

The shared Kokoro 1.4 server exposes `POST /voices/install-english`. NPCVO can call this automatically at startup or manually with `npcvo_voice_install`.

## Social accent bias

Approximate base UK weighting before race modifiers:

- Noble: 85%
- Scholar: 68%
- Guild: 55%
- Supernatural: 50%
- Merchant: 40%
- Child: 35%
- Commoner: 18%
- Underworld: 12%

High Elves add +25 points, Dark Elves +20, Wood Elves +15. Breton is mildly UK-favored; Nord and Redguard are mildly US-favored. The final weighted pool is deterministic per NPC.

## Fantasy processing

`Fantasy Processing` scales the DSP fields below without changing authored pre-generated WAVs.

- `gravel`: parallel dry rasp channel
- `saturation`: mild nonlinear harmonic color
- `presence`: upper-mid consonant/presence emphasis
- `compression`: lightweight dynamic compression
- `doubleMix`: amount of a delayed doubled layer
- `doublePitch`: pitch offset of doubled layer
- `doubleDelayMs`: delay of doubled layer

Male Dark Elves receive the dedicated dry/gravel treatment by default. Orc, Khajiit, Argonian, and Dragon profiles are progressively more exaggerated. Humans and most Mer remain restrained.


## v0.1.8 creature DSP fields

Argonian profiles can use `subharmonicMix`, `subharmonicPitch`, `hiss`, `throatResonance`, `flutterDepth`, `flutterRate`, and `croak`. Khajiit profiles can use `purrMix`, `purrRate`, `felineResonance`, `growl`, and `breath`. These fields are scaled by Fantasy Processing just like the older character DSP fields.

Male Orc and Dunmer profiles also use curated low-register Kokoro pools before pitch/DSP. Female Dunmer use a curated lower female pool and a deeper, smoother baseline.

## Custom VoiceTypes

Every generated key remains an ordinary profile and can be overridden in persistent `VoiceTypes.json`, for example:

```json
{
  "key": "DarkElfMaleNoble",
  "voices": ["bm_george", "bm_fable"],
  "speed": 0.94,
  "pitch": -0.35,
  "gravel": 0.5,
  "saturation": 0.1,
  "presence": 0.2,
  "compression": 0.15,
  "doubleMix": 0.0,
  "doublePitch": 0.0,
  "doubleDelayMs": 0.0,
  "lockVariation": false,
  "enabled": true
}
```

Profile files now carry a schema version. On the v2 upgrade, NPCVO creates a one-time `.bak` of the previous built-in file, refreshes built-in profile keys to the v0.1.8 defaults, and keeps non-built-in custom keys. Subsequent loads merge missing profiles normally.

## v0.1.7 special and delivery layers

`SpecialVoices.json` is now a separate curated layer between user `UniqueVoices.json` and portrait/procedural resolution. It uses the same profile schema plus:

- `defaultEmotion`
- `spectral`
- `reverb`

The emotion layer is optional and applies after the base voice/profile is resolved. Grammar Polish is also optional and never changes the original string used for VoicePack hashes.

See `SPECIAL-NPC-VOICE-RESEARCH.md` for the starter story/Daedra cast.

## v0.1.7.1 console audition commands

Use `npcvo_test_voice <race> <gender> [role]` to audition the exact procedural resolver without finding an NPC in-game. Race aliases accept Dunmer/Altmer/Bosmer and M/F shorthand. Convenience commands are also registered for every built-in race/gender pair (`npcvo_test_breton_m`, `npcvo_test_breton_f`, etc.).

`npcvo_test_daedra` uses a deliberately generic spectral/reverb fallback rather than any named Daedric Prince profile, so it is useful for tuning the shared supernatural processing layer.

Race/gender shortcut tests are forced to Neutral emotion so comparisons reflect the VoiceType itself. The Daedra test deliberately uses Threatening emotion to demonstrate the spectral fallback in a plausible delivery state.
