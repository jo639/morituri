"""정합 시험 자산([15]§10.29).

    python make_test_assets.py

로더가 좌표를 제대로 물리는지 **그림 없이 검증**하기 위한 더미 세트다.
  plate_back  : 각 구역을 색으로 칠한 판   → 채움이 이 색 위에 정확히 얹히면 정합 성공
  plate_front : 구역 테두리만, 안쪽은 알파 0 → 비면 채움이 보이고, 안 비면 가려진다
Morituri.Headless/hud/test/ 에 떨어뜨리고 ?hud=test/ 로 연다.
"""
import io, json, os, shutil
from PIL import Image, ImageDraw
from make_guide import LAYOUT, S, MAGENTA, CYAN, AMBER

HERE = os.path.dirname(os.path.abspath(__file__))
DEST = os.path.abspath(os.path.join(HERE, "..", "..", "Morituri.Headless", "hud", "test"))


def plate():
    L = LAYOUT
    W, H = L["plate"]["w"] * S, L["plate"]["h"] * S
    back = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    front = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    db, df = ImageDraw.Draw(back), ImageDraw.Draw(front)

    db.rectangle([0, 0, W - 1, H - 1], fill=(26, 20, 16, 235))
    df.rectangle([2, 2, W - 3, H - 3], outline=(255, 255, 255, 220), width=4)

    p = L["portrait"]
    r = [(p["cx"] - p["r"]) * S, (p["cy"] - p["r"]) * S, (p["cx"] + p["r"]) * S, (p["cy"] + p["r"]) * S]
    db.ellipse(r, fill=MAGENTA + (90,))
    df.ellipse(r, outline=MAGENTA + (255,), width=5)

    for b in L["bars"]:
        r = [b["x"] * S, b["y"] * S, (b["x"] + b["w"]) * S, (b["y"] + b["h"]) * S]
        db.rectangle(r, fill=MAGENTA + (90,))
        df.rectangle(r, outline=MAGENTA + (255,), width=3)

    ic = L["icons"]
    for i in range(ic["count"]):
        gx = ic["x"] + i * (ic["size"] + ic["gap"])
        r = [gx * S, ic["y"] * S, (gx + ic["size"]) * S, (ic["y"] + ic["size"]) * S]
        db.rectangle(r, fill=CYAN + (70,))
        df.rectangle(r, outline=CYAN + (255,), width=3)

    b = L["badge"]
    r = [(b["cx"] - b["r"]) * S, (b["cy"] - b["r"]) * S, (b["cx"] + b["r"]) * S, (b["cy"] + b["r"]) * S]
    df.ellipse(r, outline=AMBER + (255,), width=3)
    return back, front


def center():
    L = LAYOUT
    W, H = L["center"]["w"] * S, L["center"]["h"] * S
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rectangle([2, 2, W - 3, H - 3], fill=(26, 20, 16, 225), outline=(255, 255, 255, 220), width=4)
    n = L["clock"]["num"]
    d.rectangle([(n["x"] - n["w"] / 2) * S, n["y"] * S, (n["x"] + n["w"] / 2) * S, (n["y"] + n["h"]) * S],
                outline=MAGENTA + (255,), width=3)
    return im


if __name__ == "__main__":
    if os.path.isdir(DEST):
        shutil.rmtree(DEST)
    os.makedirs(DEST)
    b, f = plate()
    b.save(os.path.join(DEST, "plate_back.png"))
    f.save(os.path.join(DEST, "plate_front.png"))
    center().save(os.path.join(DEST, "center.png"))
    m = json.load(io.open(os.path.join(HERE, "hud.sample.json"), encoding="utf-8"))
    m["portrait"]["shape"] = "circle"
    io.open(os.path.join(DEST, "hud.json"), "w", encoding="utf-8").write(
        json.dumps(m, indent=2, ensure_ascii=False))
    print("→", DEST)
    for fn in sorted(os.listdir(DEST)):
        print("  ", fn)
