# NPCVO Voice Types v0.1.8.1

## Heroic Nord male

Male Nords no longer use the generic male pool. Their procedural profiles are anchored by `am_granite` from the CC0 `n33kos/kokoro-voices` collection and blended with stock British Kokoro males.

Typical blend specifications are:

- `am_granite,am_granite,bm_george`
- `am_granite,am_granite,bm_daniel`
- `am_granite,am_granite,bm_fable`

Kokoro averages comma-separated voice tensors. Repeating Granite gives it about two-thirds of the blend. Higher-class Nord roles can draw from blends with a larger British share. The English phoneme pipeline is still chosen from the first `a`-prefixed voice, avoiding cross-language G2P experiments.

Baseline male Nord processing: approximately `-0.85` semitone, `0.95x` race cadence before social-role modifiers, light gravel/saturation, moderate presence/compression, and a very small double layer.

## Community library

Shared Kokoro v1.5.1 manages only one community voice:

- `am_granite`

The five other n33kos voices previously exposed by v0.1.8.1 are intentionally excluded. Official Kokoro English voices remain available alongside Granite, and comma-separated blends still work.
