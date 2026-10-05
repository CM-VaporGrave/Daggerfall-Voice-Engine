# NPCVO v0.1.7 — Special NPC Voice Research

These are **curated fallback directions**, not claims about canonical voice actors. Exact pre-generated WAVs and user `UniqueVoices.json` overrides remain higher priority. The intent is to keep important characters recognizable when an authored line is missing or a quest/mod adds new text.

## Research basis

- UESP/Daggerfall character pages were used for identity, rank, race, relationships, and main-quest role where available.
- Daggerfall Unity quest-source text was used when it gives stronger evidence about speaking style (for example Queen Akorithi’s direct/secretive quest offer).
- Daedric profiles combine Daggerfall quest role/dialogue with restrained series-consistent characterization. Daggerfall-era characterization can differ from later games, so every profile is intentionally overrideable.

## Story and court characters

| Character | Kokoro fallback | Default delivery | Direction | Research |
|---|---|---|---|---|
| Lady Brisienna | `bf_alice` | Formal | Nord noble; covert Imperial Intelligence/Blades agent and ambassador. Controlled, educated, discreet delivery. | https://en.uesp.net/wiki/Daggerfall:Lady_Brisienna |
| King Lysandus | `bm_george` | Sad | Dead king whose unresolved murder drives the main quest. Regal base with ghostly/sorrowful processing. | https://en.uesp.net/wiki/Daggerfall:Lysandus |
| Queen Mynisera | `bf_isabella` | Formal | Dowager queen tied to the Emperor and the missing-letter investigation. Reserved courtly delivery. | https://en.uesp.net/wiki/Daggerfall:Mynisera |
| King Gothryd | `bm_daniel` | Formal | Young King of Daggerfall and Lysandus son; political central figure. Firm aristocratic delivery. | https://en.uesp.net/wiki/Daggerfall:Gothryd |
| Queen Aubk-i | `bf_lily` | Friendly | Queen of Daggerfall and Sentinel royal by birth. High-class but warmer presentation. | https://en.uesp.net/wiki/Daggerfall:Aubk-i |
| Medora Direnni | `bf_alice` | Formal | Former Daggerfall court sorceress and Lysandus ally/lover; capable, restrained magical-aristocratic tone. | https://en.uesp.net/wiki/Daggerfall:Medora_Direnni |
| Nulfaga | `bf_isabella` | Sad | Renowned mystic and Lysandus mother, destabilized by grief. Slower eccentric/sorrowful delivery. | https://en.uesp.net/wiki/Daggerfall:Nulfaga |
| Gortwog | `bm_daniel` | Formal | Orc noble, warlord/king and ruler of Orsinium. Deep disciplined baritone with educated authority rather than monster growl. | https://en.uesp.net/wiki/Daggerfall:Gortwog |
| Queen Akorithi | `bf_isabella` | Formal | Queen of Sentinel; quest dialogue is direct, commanding and deliberately secretive about her motives. | https://github.com/Interkarma/daggerfall-unity/blob/master/Assets/StreamingAssets/Quests/S0000017.txt |
| Prince Lhotun | `bm_fable` | Sad | Sentinel prince central to the missing-prince investigation; subdued youthful-noble fallback. | https://en.uesp.net/wiki/Daggerfall:Lhotun |
| King Eadwyre | `bm_lewis` | Formal | King of Wayrest and major Totem claimant. Older controlled court voice. | https://en.uesp.net/wiki/Daggerfall:Eadwyre |
| Queen Barenziah | `bf_emma` | Friendly | Experienced Dark Elf queen and political survivor in Wayrest. Deep, warm, sultry and composed Dunmer delivery. | https://en.uesp.net/wiki/Daggerfall:Barenziah |
| Prince Helseth | `bm_daniel` | Formal | Dark Elf noble and Barenziah son; deep, dry, gravel-heavy Dunmer male treatment with aristocratic control. | https://en.uesp.net/wiki/Daggerfall:Helseth |
| Princess Morgiah | `bf_isabella` | Formal | Dark Elf princess of Wayrest and Barenziah daughter. Polished, politically capable, deeper and smoother Dunmer female tone. | https://en.uesp.net/wiki/Daggerfall:Morgiah |
| Princess Elysana | `bf_isabella` | Friendly | Wayrest princess involved in court intrigue. Bright aristocratic surface delivery, left overridable by authored VO. | https://en.uesp.net/wiki/Daggerfall:Elysana |
| Lord Woodborne | `bm_lewis` | Threatening | Wayrest noble and principal antagonist connected to Lysandus murder. Controlled threatening fallback. | https://en.uesp.net/wiki/Daggerfall:Lord_Woodborne |
| The Underking | `bm_daniel` | Formal | Ancient undead power and Totem claimant. Very slow, resonant, spectral processing. | https://en.uesp.net/wiki/Daggerfall:Underking |
| The King of Worms | `bm_fable` | Threatening | Arch-necromancer and Totem claimant. Cultured, cold supernatural delivery rather than simple monster growl. | https://en.uesp.net/wiki/Daggerfall:King_of_Worms |
| Emperor Uriel Septim VII | `bm_george` | Formal | Emperor who dispatches the Agent to the Iliac Bay. Deliberate authoritative imperial tone. | https://en.uesp.net/wiki/Daggerfall:Introduction |
| Prince Greklith | `am_onyx` | Formal | Sentinel prince and Akorithi son. Strong younger Redguard royal fallback. | https://en.uesp.net/wiki/Daggerfall:Greklith |

## Daedric Princes

All Daedric fallbacks add the new **spectral** post-process to varying degrees. The effect keeps the dry Kokoro signal dominant, then adds short detuned/delayed layers plus a restrained dialogue-safe reverb tail.

| Prince | Kokoro fallback | Default delivery | Direction |
|---|---|---|---|
| Azura | `bf_emma` | Formal | Regal, distant and otherworldly; luminous spectral treatment. |
| Boethiah | `bm_fable` | Threatening | Competitive and dangerous; controlled menace. |
| Clavicus Vile | `bm_fable` | Friendly | Smooth, conversational deal-maker with an uncanny edge. |
| Hermaeus Mora | `bm_george` | Formal | Very slow, deep, layered and heavily spectral. |
| Hircine | `am_onyx` | Threatening | Hunter archetype; grounded, predatory weight. |
| Malacath | `am_onyx` | Angry | Gruff, heavy, confrontational supernatural voice. |
| Mehrunes Dagon | `am_fenrir` | Threatening | Forceful destructive presence with strong spectral/gravel processing. |
| Mephala | `bf_alice` | Threatening | Controlled, intimate and dangerous rather than loud. |
| Meridia | `bf_isabella` | Formal | Bright authoritative supernatural presentation. |
| Molag Bal | `am_onyx` | Threatening | Deepest, slowest heavy spectral profile; controlled dominance. |
| Namira | `af_kore` | Threatening | Low, unsettling, textured spectral delivery. |
| Nocturnal | `bf_emma` | Formal | Dark, restrained, reverberant supernatural tone. |
| Peryite | `bm_lewis` | Formal | Measured, dry supernatural delivery. |
| Sanguine | `bm_fable` | Friendly | Livelier, amused supernatural delivery. |
| Sheogorath | `bm_fable` | Excited | Fast, unstable energetic fallback; intentionally easy to replace with authored VO. |
| Vaernima | `af_nicole` | Threatening | Dreamlike, slow, heavily spectral/reverberant delivery. |

## Pre-generated priority and canonical folders

Canonical authored lines live at:

`VoicePack/Characters/<slug>/Lines/<SHA1>.wav`

The SHA1 is computed from NPCVO’s normalized **original DFU line**, preserving compatibility even when Grammar Polish is enabled. Legacy `VoicePack/<NPC_NAME>/<SHA1>.wav` and portrait-key paths remain supported.


## v0.1.8.3 Granite recast notes

Granite is now used selectively outside Nords where its low, deliberate base supports an established character archetype. Lower/cleaner Granite blends are used for heroic or imposing figures; UK-heavy blends for arcane, kingly, or wizard-like figures; and slightly brighter Granite blends for schemers such as Woodborne or Clavicus Vile. Pre-generated WAVs still override every fallback choice.

## v0.1.9 source/persistence note

Authored special-character WAVs remain the highest-priority exact line source in Hybrid mode. They are engine-agnostic files under the established VoicePack structure; Kokoro remains the only runtime synthesizer. Procedural NPC persistence is handled separately by `VoiceAssignments.json` and does not change canonical special-character hashing.
