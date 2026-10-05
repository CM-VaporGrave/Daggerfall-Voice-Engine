NPCVO PRE-GENERATED VOICEPACK OVERRIDES
=======================================

In Hybrid mode, an exact pre-generated WAV takes priority over Kokoro. In
Pre-generated WAV only mode, only matching authored WAVs speak; missing lines simply
remain vanilla text. VoicePack WAVs can come from any offline synthesis, recording, or
audio-production workflow and require no additional runtime voice tool.

Installable pack layout:

  DaggerfallUnity_Data\StreamingAssets\Sound\NPCVO\VoicePack\<NPC_NAME>\<LINE_HASH>.wav

Example:

  ...\VoicePack\Nulfaga\0123456789abcdef0123456789abcdef01234567.wav

At runtime, use the console command:

  npcvo_lineinfo

after a line appears. It prints the exact normalized script, SHA1 hash, and expected
relative WAV path. This is the safest way to author a matching file.

You can also use VoicePackTools\PrepareVoicePackLine.py while producing a pack.

Player name handling:
When "Ignore player name in VoicePack keys" is enabled (default), NPCVO removes the
player's name from the hash key. That allows a prerecorded unique NPC line to omit the
player's name while the on-screen game text remains unchanged.

Do not omit dynamic locations, item names, amounts, directions, dates, or other content
that changes the actual meaning of a line. Leave those lines to real-time Kokoro unless
you have authored an exact variant.

VoicePack WAVs are permanent and are NEVER touched by NPCVO cache cleanup.

NPCVO v0.1.7 canonical special-character path:
  VoicePack/Characters/<slug>/Lines/<SHA1>.wav
See Characters/manifest.json and SPECIAL-NPC-VOICE-RESEARCH.md. Legacy folders remain supported.
