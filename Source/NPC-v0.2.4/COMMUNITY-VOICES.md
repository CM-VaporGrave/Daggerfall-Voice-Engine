# NPCVO Community Kokoro Voice Sources

## Integrated: am_granite only

NPCVO intentionally manages only `am_granite` from `n33kos/kokoro-voices`. The other voices from that repository are not downloaded or used by built-in NPCVO profiles. Shared Kokoro v1.5.1 therefore manages 29 English voices: 28 official Kokoro English voices plus Granite. If Granite cannot be installed, the shared server substitutes `am_fenrir` so dialogue still works.

Granite is now part of the broader male voice ecosystem rather than a Nord-only special case. NPCVO uses it sparingly and in blends:

- **Epic / badass:** raw or US-heavy Granite, usually with a modestly lower profile pitch.
- **Wizard / sage / old-world:** Granite blended toward `bm_george`, `bm_daniel`, or `bm_fable`.
- **Slimy schemer:** raw Granite or a lighter Granite blend, sometimes with slightly quicker/brighter profile delivery.
- **Heroic Nord:** remains heavily Granite-weighted with a restrained UK component.

The default procedural tables only introduce Granite at low-to-moderate probability outside Nords so Breton, Redguard, Altmer, and Bosmer populations do not collapse into one voice. Beast races, Dunmer, and Orcs keep their dedicated racial pools unless a named-character profile explicitly overrides them.

## External custom-voice tooling

NPCVO no longer bundles training/corpus-generation utilities. If you want to create or experiment with Kokoro-compatible voicepacks outside the mod, useful external projects include:

- `n33kos/kokoro-voice-designer` - designs and exports synthetic Kokoro `.pt` voicepacks by manipulating voice/style space.
- `gushilabs/train-kokoro-encoder-styletts2` - experimental WAV-to-Kokoro voicepack extraction using retrained StyleTTS2-style encoders. Its authors note that zero-shot cloning quality is still experimental.
- Standard Kokoro CLI/server tools can render an existing `.pt` voice or blend to WAV if you need a synthetic corpus, but that is separate from recovering a voicepack's original training recordings.

The shared NPCVO installer remains deliberately conservative: only Granite is fetched automatically. Additional custom `.pt` files can still be used manually where the local Kokoro backend supports them.
