"""
베이크 산출물(.anim.json)을 뷰어가 읽는 sprites 시트 JSON에 합친다.

  py merge_anim.py --anim ../../Morituri.Headless/sprites/bake/walk_fwd.anim.json

기존 animations 항목만 갱신하고 정적 포즈(frames)는 건드리지 않는다.
덮어쓰기 전 타임스탬프 백업을 남긴다.
"""
import argparse, json, os, shutil, time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_TARGET = os.path.normpath(
    os.path.join(HERE, "..", "..", "Morituri.Headless", "sprites", "sprites.json"))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--anim", required=True, help="bake.py가 만든 <name>.anim.json")
    p.add_argument("--target", default=DEFAULT_TARGET, help="합칠 시트 JSON (기본: sprites.json)")
    a = p.parse_args()

    with open(a.anim, encoding="utf-8") as f:
        incoming = json.load(f)
    with open(a.target, encoding="utf-8") as f:
        sheet = json.load(f)

    bak = f"{a.target}.{time.strftime('%Y%m%d_%H%M%S')}.bak"
    shutil.copy2(a.target, bak)

    sheet.setdefault("animations", {}).update(incoming)
    with open(a.target, "w", encoding="utf-8") as f:
        json.dump(sheet, f, ensure_ascii=False, indent=2)

    print(f"[merge] {', '.join(incoming)} → {os.path.basename(a.target)}  (백업 {os.path.basename(bak)})")


if __name__ == "__main__":
    main()
