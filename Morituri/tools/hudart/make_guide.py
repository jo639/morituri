"""HUD 크롬 자산 정합 가이드([15]§10.29).

    python make_guide.py

라니스타가 프레임 그림을 그릴 때 **좌표를 맞추기 위한 밑판**을 뽑는다.
이미지 생성에 레퍼런스로 넣어도 되고, 포토샵에서 아래 레이어로 깔아도 된다.

규칙은 하나뿐이다: **마젠타로 칠한 곳은 알파 0으로 비워야 한다.**
거기로 HP·스태미나·가드 채움과 초상 스프라이트가 올라온다. 덮으면 안 보인다.

좌표는 여기 값을 그대로 지킬 필요 없다 — 한 벌 안에서 일관되기만 하면
뷰어 쪽 hud.json을 완성된 그림에 맞춰 다시 잰다.

안내 띠는 **캔버스 아래에 덧붙인 것**이라 실제 자산 크기에 포함되지 않는다
(초판은 안내문을 캔버스 안에 넣었다가 아이콘 행과 겹쳤다).
"""
import io, json, os
from PIL import Image, ImageDraw, ImageFont

S = 3                       # 배율. 논리 px × S = 실제 px
BAND = 30                   # 캔버스 밖 안내 띠 높이(실제 px)
HERE = os.path.dirname(os.path.abspath(__file__))
KR = "C:/Windows/Fonts/malgun.ttf"

# 뷰어의 기본 배치(= 지금 절차 생성 값). 논리 px, 판 바깥 모서리 기준.
LAYOUT = {
    "plate":    {"w": 362, "h": 96},
    "portrait": {"cx": 32, "cy": 38, "r": 30},
    "badge":    {"cx": 32, "cy": 70, "r": 10},
    "name":     {"x": 92, "y": 12, "w": 250},
    "bars": [{"x": 92, "y": 17, "w": 250, "h": 17, "t": "HP"},
             {"x": 92, "y": 38, "w": 232, "h": 12, "t": "STAMINA"},
             {"x": 92, "y": 53, "w": 214, "h": 12, "t": "GUARD"}],
    "icons":    {"x": 92, "y": 67, "size": 24, "gap": 6, "count": 7},
    "center":   {"w": 240, "h": 84},
    "clock": {"label": {"x": 120, "y": 8, "w": 60, "h": 10},
              "num":   {"x": 120, "y": 26, "w": 74, "h": 30},
              "crest": {"x": 120, "y": 62, "w": 26, "h": 18}},
}

MAGENTA = (255, 0, 170)
CYAN    = (0, 210, 255)
AMBER   = (255, 190, 60)


def font(px):
    try:
        return ImageFont.truetype(KR, px)
    except Exception:
        return ImageFont.load_default()


def canvas(w, h):
    """실제 캔버스(격자 깔린 어두운 판) + 그 위에 얹을 투명 레이어."""
    base = Image.new("RGBA", (w, h + BAND), (10, 8, 7, 255))
    d = ImageDraw.Draw(base)
    d.rectangle([0, 0, w - 1, h - 1], fill=(20, 16, 14, 255))
    for gx in range(0, w, 15 * S):                 # 15 논리 px 격자
        d.line([gx, 0, gx, h], fill=(255, 255, 255, 16))
    for gy in range(0, h, 15 * S):
        d.line([0, gy, w, gy], fill=(255, 255, 255, 16))
    return base, Image.new("RGBA", (w, h + BAND), (0, 0, 0, 0))


def box(d, x, y, w, h, col, a=52):
    d.rectangle([x * S, y * S, (x + w) * S, (y + h) * S], fill=col + (a,), outline=col + (255,), width=2)


def circle(d, cx, cy, rr, col, a=52):
    d.ellipse([(cx - rr) * S, (cy - rr) * S, (cx + rr) * S, (cy + rr) * S],
              fill=col + (a,), outline=col + (255,), width=2)


def finish(base, over, w, h, legend, out):
    im = Image.alpha_composite(base, over)         # 알파 합성은 여기서 한 번에
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, w - 1, h - 1], outline=(255, 255, 255, 150), width=2)
    d.text((6, h + 6), legend, font=font(13), fill=(238, 228, 208, 255))
    im.save(out)
    return out, (w, h)


def make_plate():
    L = LAYOUT
    W, H = L["plate"]["w"] * S, L["plate"]["h"] * S
    base, over = canvas(W, H)
    d = ImageDraw.Draw(over)
    f = font(16)

    p, b, n, ic = L["portrait"], L["badge"], L["name"], L["icons"]
    circle(d, p["cx"], p["cy"], p["r"], MAGENTA)
    circle(d, b["cx"], b["cy"], b["r"], CYAN)
    d.line([n["x"] * S, n["y"] * S, (n["x"] + n["w"]) * S, n["y"] * S], fill=AMBER + (220,), width=2)
    for r in L["bars"]:
        box(d, r["x"], r["y"], r["w"], r["h"], MAGENTA)
    for i in range(ic["count"]):
        box(d, ic["x"] + i * (ic["size"] + ic["gap"]), ic["y"], ic["size"], ic["size"], CYAN, 0)

    # 라벨은 각 요소 **안쪽**에만 — 초판은 바깥에 찍었다가 이웃 요소를 덮었다
    d.text((p["cx"] * S - 34, p["cy"] * S - 8), "PORTRAIT", font=f, fill=MAGENTA + (255,))
    d.text((n["x"] * S + 4, n["y"] * S - 21), "NAME", font=f, fill=AMBER + (255,))
    for r in L["bars"]:
        d.text((r["x"] * S + 6, (r["y"] + r["h"] / 2) * S - 9), r["t"], font=f, fill=MAGENTA + (255,))
    d.text(((ic["x"] + ic["count"] * (ic["size"] + ic["gap"])) * S + 4,
            (ic["y"] + ic["size"] / 2) * S - 9), "×%d" % ic["count"], font=f, fill=CYAN + (255,))

    return finish(base, over, W, H,
                  "마젠타 = 알파 0 으로 비울 것(채움·초상이 올라온다)   ·   시안 = 칸 테두리만   ·   "
                  "격자 15논리 px   ·   자산 크기 %d×%d (%d배) — 이 안내 띠는 자산에 포함 안 됨" % (W, H, S),
                  os.path.join(HERE, "guide_plate.png"))


def make_center():
    L = LAYOUT
    W, H = L["center"]["w"] * S, L["center"]["h"] * S
    base, over = canvas(W, H)
    d = ImageDraw.Draw(over)
    f = font(16)
    for key, col, lab, a in (("label", AMBER, "TEMPVS", 0), ("num", MAGENTA, "NUMBER", 52),
                             ("crest", CYAN, "CREST", 0)):
        r = L["clock"][key]
        box(d, r["x"] - r["w"] / 2, r["y"], r["w"], r["h"], col, a)
        d.text(((r["x"] - r["w"] / 2) * S + 5, (r["y"] + r["h"] / 2) * S - 9), lab, font=f, fill=col + (255,))
    return finish(base, over, W, H,
                  "마젠타 = 숫자가 올라올 자리(비울 것)   ·   날개·장식은 캔버스 안에서 자유   ·   "
                  "자산 크기 %d×%d (%d배)" % (W, H, S),
                  os.path.join(HERE, "guide_center.png"))


def make_manifest():
    """뷰어가 읽을 hud.json 초안 — 그림이 이 배치를 따랐다면 그대로 쓴다."""
    L = LAYOUT
    m = {
        "scale": S,
        "plate": {"back": "plate_back.png", "front": "plate_front.png",
                  "w": L["plate"]["w"], "h": L["plate"]["h"], "mirrorForB": True},
        "center": {"img": "center.png", "w": L["center"]["w"], "h": L["center"]["h"]},
        "portrait": dict(L["portrait"], shape="circle"),
        "badge": dict(L["badge"], show=True),
        "name": {"x": L["name"]["x"], "y": L["name"]["y"]},
        "bars": [{k: r[k] for k in ("x", "y", "w", "h")} for r in L["bars"]],
        "icons": {k: L["icons"][k] for k in ("x", "y", "size", "gap")},
    }
    out = os.path.join(HERE, "hud.sample.json")
    io.open(out, "w", encoding="utf-8").write(json.dumps(m, indent=2, ensure_ascii=False))
    return out


if __name__ == "__main__":
    for fn in (make_plate, make_center):
        p, sz = fn()
        print("%s  %dx%d (+안내 %dpx)" % (os.path.basename(p), sz[0], sz[1], BAND))
    print(os.path.basename(make_manifest()))
