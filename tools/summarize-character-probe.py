"""Summarize a CharacterProbeMod capture without requiring the game to be running."""
import argparse
import collections
import json
from pathlib import Path

DEFAULT = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Astral Party\8vJXn6CN\AstralParty_ModLoader\mods\CharacterProbeMod\captures")

def summarize(folder):
    counts = collections.Counter()
    skins, clips, sprites, actors = {}, {}, {}, {}
    ui = set()
    progress = set()
    errors = {}
    changes = collections.Counter()
    states = collections.defaultdict(set)
    partial = 0
    with (folder / "observations.jsonl").open(encoding="utf-8-sig") as stream:
        for line in stream:
            if not line.strip():
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                # A running process may currently be appending a line. Report this explicitly.
                partial += 1
                continue
            kind = row.get("kind", "unknown")
            counts[kind] += 1
            if kind == "skin":
                skins[(row["player"], row["hero"], row["skinItem"])] = row
            elif kind == "clip":
                clips[row["clipId"]] = row
            elif kind == "sprite":
                sprites[row["spriteId"]] = row
            elif kind == "actor":
                actors[row["actor"]] = row
            elif kind == "uiImage":
                ui.add((row.get("parentType"), row.get("path"), row.get("url")))
                if row.get("parentType") == "UI.UIOperationTime_Progress":
                    progress.add(row.get("url"))
            elif kind == "progressAvatar":
                progress.add(row.get("url"))
            elif kind == "spriteChange":
                changes[row["actor"]] += 1
            elif kind == "animationState":
                states[row["actor"]].add(row.get("clips", ""))
            elif kind == "error":
                key = (row.get("area"), row.get("exception"), row.get("message"))
                errors[key] = row
    atlases = collections.defaultdict(list)
    for row in sprites.values():
        atlases[(row.get("textureId"), row.get("texture"), row.get("textureWidth"), row.get("textureHeight"))].append(row)
    status = None
    try:
        status = json.loads((folder / "status.json").read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        pass
    return {
        "capture": str(folder), "status": status, "counts": dict(counts), "invalidOrPartialLines": partial,
        "skins": list(skins.values()), "progressAvatarUrls": sorted(x for x in progress if x),
        "actors": [{**a, "observedSpriteChanges": changes.get(i, 0), "observedClips": sorted(states[i])} for i, a in actors.items()],
        "clips": list(clips.values()),
        "atlases": [{"textureId": k[0], "name": k[1], "width": k[2], "height": k[3], "sprites": sorted(v, key=lambda s: s.get("name", ""))} for k, v in atlases.items()],
        "uiImages": [{"parentType": a, "path": b, "url": c} for a, b, c in sorted(ui, key=lambda x: x[1])],
        "errors": list(errors.values()),
        "limitations": "Runtime sampling contains only rendered/observed sprites and base-layer state samples. It is not an exhaustive sprite export or original animation timing specification."
    }

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture", nargs="?", type=Path, help="Capture session directory; defaults to newest installed session")
    parser.add_argument("--output", type=Path, help="Optional JSON report destination")
    args = parser.parse_args()
    folder = args.capture
    if folder is None:
        sessions = sorted((p for p in DEFAULT.glob("*") if p.is_dir() and (p / "observations.jsonl").exists()), key=lambda p: p.name)
        if not sessions:
            parser.error("No capture yet. Start the game with CharacterProbeMod and wait 30 seconds.")
        folder = sessions[-1]
    report = summarize(folder)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ["capture", "status", "counts", "invalidOrPartialLines", "progressAvatarUrls", "errors"]}, ensure_ascii=False, indent=2))
    print("Characters:", [(s["hero"], s["skinItem"], s["label"], s.get("characterMap")) for s in report["skins"]])
    print("Clips:", [(c["name"], c["length"], c["frameRate"]) for c in report["clips"]])
    print("Atlases:", [(a["name"], a["width"], a["height"], len(a["sprites"])) for a in report["atlases"]])
    if args.output:
        print("Report:", args.output.resolve())

if __name__ == "__main__":
    main()
