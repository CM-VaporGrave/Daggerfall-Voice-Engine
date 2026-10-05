# Dynamic Portraits compatibility (Nexus 1178)

NPCVO's Dynamic Portraits integration is intentionally soft and reflection-only. There is no compile-time dependency and no Harmony patch collision.

## v0.1.7 emotion pairing

NPCVO can now read Dynamic Portraits' current reaction/mood state through reflection. For ordinary TalkWindow dialogue, Dynamic Portraits **Win** is mapped to Friendly delivery, **Loss** to Angry delivery, `_Hap` to Friendly, and `_Ang` to Angry. This signal outranks NPCVO's text heuristics when **Dynamic Portraits > Emotion Sync** is enabled.

The bridge is intentionally read-only. Dynamic Portraits owns its animation/reputation state; NPCVO only uses that state to choose vocal delivery. This avoids forcing private portrait state or fighting its own reaction timers.

## Normal TalkWindow

For ordinary conversations, NPCVO can read Dynamic Portraits' active base portrait identity and convert it to a stable key such as:

- `TFAC00I0.RCI_10`
- `FACES.CIF_42`
- `CHLD00I0.RCI_3`

Expression/mood/animation tags are deliberately not part of the voice key. A character should retain the same voice while the portrait changes expression.

The portrait key can be targeted in `PortraitVoices.json`.

NPCVO also detects Dynamic Portraits' portrait AudioSource and can:

1. Ignore portrait audio
2. Defer NPCVO while portrait audio plays (default)
3. Skip the NPCVO line if portrait audio is already playing

## v0.1.3.2 QuestOffer portrait UI

DFU's QuestOffer path is separate from the normal TalkWindow. NPCVO v0.1.3.2 now supplies its own static portrait/name/dialogue presentation for quest-giver offers while leaving the underlying native QuestOffer/message-box controls intact.

The quest portrait is resolved from the same DFU face data used by normal Talk/Ask dialogue. NPCVO first checks DFU's standard CIF/RCI texture-replacement path and then falls back to Arena2 art.

### Current behavior

Dynamic Portraits is not yet asked to animate this quest portrait. Existing Dynamic Portraits implementations are centered on `DaggerfallTalkWindow`, whereas NPCVO's quest presentation lives in a QuestOffer/message-box panel.

This means:

- Dynamic Portraits should continue animating normal TalkWindow portraits as before.
- NPCVO v0.1.3.2 quest offers reuse the portrait texture currently assigned to DFU's TalkWindow.
- A compatible ordinary CIF/RCI texture replacement may still be used through DFU's normal `TextureReplacement` path.

### Compatibility seam for a future bridge

While NPCVO's quest portrait is active, these public static properties are available:

```csharp
NPCVO.NPCVOMod.ActiveQuestPortraitPanel
NPCVO.NPCVOMod.ActiveQuestPortraitCif
NPCVO.NPCVOMod.ActiveQuestPortraitRecordId
```

They expose:

- the exact `Panel` that should receive the portrait/animation
- the resolved CIF/RCI filename
- the resolved record ID

A Dynamic Portraits-side compatibility patch can therefore redirect its animation renderer into that panel, or NPCVO can later add a reflection adapter around the exposed panel/texture. Either approach can reuse the existing v0.1.3.2 quest UI rather than replacing the quest window a second time.

## Why NPCVO does not replace QuestOffer wholesale

QuestOffer classes and their message boxes own quest callbacks, button events and state transitions. NPCVO keeps those objects alive and only changes presentation. This is also friendlier to mods that derive from or replace the QuestOffer implementation.

The first v0.1.3.2 test should therefore focus on whether Accept/Reject and follow-up quest messages still behave exactly like vanilla. Animated Dynamic Portraits quest heads can be layered on after that behavior is confirmed.
