# NPCVO canonical pre-generated character pack

NPCVO v0.1.7 checks this layout before legacy VoicePack folders:

`Characters/<slug>/Lines/<SHA1>.wav`

Use `../../VoicePackTools/PrepareVoicePackLine.py --character-slug <slug> ...` to place an authored WAV here. Exact pre-generated audio always wins; if a line is missing, NPCVO falls back to the character's user/curated Kokoro profile and then the normal procedural VoiceType chain.

`manifest.json` documents the included canonical slugs, aliases, fallback Kokoro voice, delivery direction, and research source used to seed the profiles.
