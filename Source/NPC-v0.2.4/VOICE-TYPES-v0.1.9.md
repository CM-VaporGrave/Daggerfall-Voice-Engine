# NPCVO Voice Types v0.1.9

This revision keeps the v0.1.8 creature DSP architecture and extends `am_granite` beyond Nords as a controlled procedural ingredient.

## Granite archetypes

- **Epic / badass:** raw or US-heavy Granite; best with neutral-to-lower pitch and firm compression.
- **Wizard / sage:** Granite with a majority British component (`bm_george`, `bm_daniel`, or `bm_fable`).
- **Schemer / oily villain:** raw or lighter Granite blends with a modestly brighter/faster delivery.
- **Heroic Nord:** Granite remains the dominant anchor with restrained British blending.

Outside Nord profiles, Granite is appended only to eligible male Breton, Redguard, High Elf, and Wood Elf role pools. The normal US/UK race/social weighting remains dominant. Dedicated Orc, Dunmer, Khajiit, Argonian, and Dragon pools remain separate.

Built-in VoiceTypes use schema v5 (adds the Judge role). Built-in SpecialVoices remain schema v3.


## v0.1.9 persistent assignments and Judge role

Procedural NPC voice selection is no longer only deterministic-at-resolution. NPCVO now persists the selected base voice plus pitch/speed variation in `VoiceAssignments.json` using a stable identity key that excludes current portrait and inferred social role. This protects character continuity across restarts and presentation changes.

`Judge` is now a first-class social role for race/gender VoiceTypes. Judge profiles use slower, lower, more formal/resonant delivery and a stronger courtly/UK weighting than ordinary nobles. Merchant service menus themselves remain silent; this does not prevent normal spoken dialogue from merchant NPCs.
