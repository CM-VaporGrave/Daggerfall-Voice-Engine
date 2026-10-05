#!/usr/bin/env python3
"""Prepare/rename a pre-generated unique-NPC WAV for NPCVO.

This is an offline authoring helper. Create or record the line with whatever audio
workflow you prefer, then point this script at the resulting WAV.

Example:
  py -3.12 PrepareVoicePackLine.py --npc "Nulfaga" --text "You have done well, Anthony." \
      --player-name "Anthony" --wav "Nulfaga_001.wav" --out "MyVoicePack"
"""
import argparse
import hashlib
import pathlib
import re
import shutil
import unicodedata


def normalize_text(text: str) -> str:
    text = text.replace("\r", " ").replace("\n", " ").replace("<--->", " ")
    return re.sub(r"\s+", " ", text).strip()


def remove_player_name(text: str, player_name: str) -> str:
    if not player_name:
        return text
    pattern = r"(?i)(^|[\s,])" + re.escape(player_name) + r"(?=\s|[,!.?;:]|$)"
    text = re.sub(pattern, r"\1", text)
    text = re.sub(r"\s+,", ",", text)
    text = re.sub(r",\s*([.!?])", r"\1", text)
    text = re.sub(r"\s{2,}", " ", text).strip(" ,")
    return text


def normalize_for_match(text: str) -> str:
    text = normalize_text(text).lower()
    out = []
    last_space = False
    for ch in text:
        cat = unicodedata.category(ch)
        keep = cat.startswith("L") or cat.startswith("N") or ch == "'"
        if keep:
            out.append(ch)
            last_space = False
        elif not last_space:
            out.append(" ")
            last_space = True
    return re.sub(r"\s+", " ", "".join(out)).strip()


def safe_name(value: str) -> str:
    # Mirrors the practical Windows-safe folder result used by NPCVO.
    value = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', value.strip() or "Unknown_NPC")
    return re.sub(r"\s+", "_", value)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--npc", default="", help="Exact in-game NPC display name (legacy folder mode, or informational when --character-slug is used)")
    ap.add_argument("--character-slug", default="", help="Canonical v0.1.7 character slug, e.g. lady-brisienna or daedra/azura")
    ap.add_argument("--text", required=True, help="Rendered line as shown in DFU")
    ap.add_argument("--player-name", default="", help="Optional player name to omit from the prerecorded voice key")
    ap.add_argument("--wav", required=True, help="Source WAV created by your offline voice/audio workflow")
    ap.add_argument("--out", default="VoicePack", help="Output VoicePack root")
    args = ap.parse_args()

    script = remove_player_name(normalize_text(args.text), args.player_name)
    normalized = normalize_for_match(script)
    digest = hashlib.sha1(normalized.encode("utf-8")).hexdigest()
    src = pathlib.Path(args.wav)
    if not src.is_file():
        raise SystemExit(f"WAV not found: {src}")
    if not args.character_slug and not args.npc:
        raise SystemExit("Provide --character-slug for canonical v0.1.7 packs, or --npc for legacy packs.")
    if args.character_slug:
        slug_parts = [safe_name(part) for part in args.character_slug.replace("\\", "/").split("/") if part.strip()]
        dest = pathlib.Path(args.out) / "Characters"
        for part in slug_parts:
            dest = dest / part
        dest = dest / "Lines" / f"{digest}.wav"
    else:
        dest = pathlib.Path(args.out) / safe_name(args.npc) / f"{digest}.wav"
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dest)
    print(f"NPC: {args.npc or args.character_slug}")
    print(f"Voice script key: {normalized}")
    print(f"SHA1: {digest}")
    print(f"Created: {dest}")


if __name__ == "__main__":
    main()
