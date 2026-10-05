# Daggerfall Narrator - NPC - Ask UI Interop

## Design rule

NPC owns speech/presentation augmentation, not the underlying TalkWindow. When Daggerfall Narrator - Player has registered a player identity, NPC can add a small persistent player portrait and compact identity labels to the existing Ask window. It does not replace the host window.

## Shared geometry

The augmentation uses one 320x200 TalkWindow geometry regardless of skin:

- existing NPC portrait/name remain where the host places them;
- a 40x40 player head is added beside the host `textlabelPlayerSays` field;
- the host player-question field is narrowed, not replaced;
- a one-line player identity appears directly below that question area;
- a compact NPC role/race line uses the narrow strip below the NPC portrait;
- `listboxTopic`, `listboxConversation`, Tone, Previous List, scrollbars, Copy to Logbook, Goodbye, and all host callbacks remain owned by the host.

This means vanilla and Advanced Dialogue use the same layout. Texture/font replacement packages can reskin the same host components without a second compatibility layout.

## Speech sequence

1. The host UI resolves the selected question and tone normally.
2. NPC observes the exact question row.
3. Player speaks that exact question with Dialogue priority. No race/personality rewrite is applied.
4. NPC defers the corresponding answer while Player dialogue speech is active.
5. NPC speaks/reveals the answer through its normal pipeline.

If Player is absent/disabled, the augmentation does not attach and NPC continues its standalone behavior.

## Advanced Dialogue

The compatibility layer is reflection-only. It intentionally does not alter Advanced Dialogue's categories, topic population, Polite/Normal/Blunt tone logic, Previous List, Copy to Logbook, answer scrolling, or Goodbye behavior.

## Grimoire-style UI replacements

The augmentation does not ship replacement TALK textures or fonts. It uses the host TalkWindow and DFU default font, so installed UI texture/font replacements remain responsible for appearance.
