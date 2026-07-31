"""
구운 포즈들을 sprites.json에 한 번에 병합.

  py merge_poses.py

- bake/<key>.anim.json 을 전부 읽어 animations에 합친다.
- walk_bwd · dash_bwd 는 굽지 않는다. Mixamo 팩에 뒷걸음 애니가 없어서,
  walk_fwd의 프레임을 **역순으로 참조**해 만든다(같은 PNG를 가리키므로 추가 용량 0).
  전용 뒷걸음/회피 애니가 생기면 DERIVED에서 빼고 실제 베이크로 교체할 것.
- 정적 frames 블록(옛 손그림 시트)은 건드리지 않는다 — 애니가 없는 포즈의 폴백으로 남는다.
"""
import json, os, shutil, time

HERE = os.path.dirname(os.path.abspath(__file__))
SPRITES = os.path.normpath(os.path.join(HERE, "..", "..", "Morituri.Headless", "sprites"))
BAKE = os.path.join(SPRITES, "bake")
TARGET = os.path.join(SPRITES, "sprites.json")

BAKED = ["idle", "walk_fwd", "guard", "light_attack", "heavy_attack",
         "hurt_light", "hurt_heavy", "down", "taunt"]
# 파생: (새 키, 원본 키, 역순여부). 같은 PNG를 참조하므로 추가 용량 0.
DERIVED = [("walk_bwd", "walk_fwd", True),
           ("dash_bwd", "walk_fwd", True),
           ("dash_fwd", "walk_fwd", False)]   # 옛 gladiator_dash 참조를 끊는다(축척이 달라 섞이면 안 됨)


def main():
    with open(TARGET, encoding="utf-8") as f:
        sheet = json.load(f)
    anims = sheet.setdefault("animations", {})

    added, missing = [], []
    for key in BAKED:
        p = os.path.join(BAKE, f"{key}.anim.json")
        if not os.path.exists(p):
            missing.append(key)
            continue
        with open(p, encoding="utf-8") as f:
            anims.update(json.load(f))
        added.append(key)

    for new_key, src_key, rev in DERIVED:
        if src_key not in anims:
            missing.append(f"{new_key}(<-{src_key})")
            continue
        src = anims[src_key]
        frames = list(reversed(src["frames"])) if rev else list(src["frames"])
        anims[new_key] = dict(src, frames=frames)
        added.append(f"{new_key}({'역순' if rev else '동일'})")

    bak = f"{TARGET}.{time.strftime('%Y%m%d_%H%M%S')}.bak"
    shutil.copy2(TARGET, bak)
    with open(TARGET, "w", encoding="utf-8") as f:
        json.dump(sheet, f, ensure_ascii=False, indent=2)

    print("merged: " + ", ".join(added))
    if missing:
        print("MISSING: " + ", ".join(missing))
    print("backup: " + os.path.basename(bak))
    tot = sum(os.path.getsize(os.path.join(BAKE, f))
              for f in os.listdir(BAKE) if f.endswith(".png"))
    print("bake png total: %.1f KB" % (tot / 1024))


if __name__ == "__main__":
    main()
