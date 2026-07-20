"""참고/*.png 원본 사진 → theme.css 의 32px 커서 4종(base64 PNG) 재생성.

    python tools/build_cursors.py

사진을 바꾸거나 각도·크기를 조정할 때 이 스크립트만 다시 돌리면 된다.
손으로 base64를 만지지 말 것 — 여기가 유일한 생성처다.
"""
import io, re, sys, base64, pathlib
from collections import deque
from PIL import Image, ImageFilter, ImageDraw, ImageEnhance

# 윈도우 콘솔 기본 코드페이지(cp949)에서는 '—' 같은 문자에 UnicodeEncodeError가 난다.
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC  = ROOT.parent / "참고"
CSS  = ROOT / "Morituri.Headless" / "theme.css"
OUTLINE = (26, 18, 8, 255)          # #1a1208
STEP = 14                            # 배경 영역성장 허용 색차

def cut_background(im):
    """테두리에서 이웃 색차로 번져 배경을 알파 0으로. 매끄러운 그라데이션 추적용."""
    im = im.convert("RGBA"); w, h = im.size; px = im.load()
    bg = [[False]*w for _ in range(h)]; q = deque()
    for x in range(w):
        for y in (0, h-1):
            if not bg[y][x]: bg[y][x] = True; q.append((x, y))
    for y in range(h):
        for x in (0, w-1):
            if not bg[y][x]: bg[y][x] = True; q.append((x, y))
    while q:
        x, y = q.popleft(); r0, g0, b0, _ = px[x, y]
        for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
            nx, ny = x+dx, y+dy
            if 0 <= nx < w and 0 <= ny < h and not bg[ny][nx]:
                r, g, b, _ = px[nx, ny]
                if abs(r-r0)+abs(g-g0)+abs(b-b0) <= STEP*3:
                    bg[ny][nx] = True; q.append((nx, ny))
    for y in range(h):
        for x in range(w):
            if bg[y][x]:
                r, g, b, _ = px[x, y]; px[x, y] = (r, g, b, 0)
    return im

def trim(im):
    b = im.getbbox(); return im.crop(b) if b else im

def to_size(im, target):
    """프리멀티플라이 → LANCZOS 축소 → 언프리멀티플라이 → 1px 어두운 테두리.
    프리멀티플라이를 빠뜨리면 투명 경계에 배경색 헤일로가 남는다."""
    im = trim(im)
    pm = Image.new("RGBA", im.size); sp, dp = im.load(), pm.load()
    for y in range(im.size[1]):
        for x in range(im.size[0]):
            r, g, b, a = sp[x, y]; f = a/255
            dp[x, y] = (int(r*f), int(g*f), int(b*f), a)
    w, h = im.size; s = (target-2)/max(w, h)
    pm = pm.resize((max(1, int(w*s)), max(1, int(h*s))), Image.LANCZOS)
    fin = Image.new("RGBA", pm.size); sp, dp = pm.load(), fin.load()
    for y in range(pm.size[1]):
        for x in range(pm.size[0]):
            r, g, b, a = sp[x, y]
            dp[x, y] = (0,0,0,0) if a == 0 else (
                min(255,int(r*255/a)), min(255,int(g*255/a)), min(255,int(b*255/a)), a)
    a = fin.getchannel("A").filter(ImageFilter.MaxFilter(3))
    sh = Image.new("RGBA", fin.size, OUTLINE); sh.putalpha(a)
    fin = Image.alpha_composite(sh, fin)
    canvas = Image.new("RGBA", (32, 32), (0,0,0,0)); canvas.paste(fin, (0,0), fin)
    return canvas

def build():
    # 창끝은 원본이 정면 수직 → 반시계 24°로 좌상향. 월계는 원본이 이미 좌상향(반전 금지).
    arrow = cut_background(Image.open(SRC/"cursor_arrow.png")).rotate(24, Image.BICUBIC, expand=True)
    laurel = cut_background(Image.open(SRC/"cursor_laurel.png"))
    out = {"--cur-arrow": (to_size(arrow, 32), "default"),
           "--cur-hand":  (to_size(laurel, 32), "pointer")}

    no = to_size(arrow, 24)
    no = ImageEnhance.Brightness(ImageEnhance.Color(no).enhance(.25)).enhance(.85)
    d = ImageDraw.Draw(no)
    d.ellipse([17.6,17.6,30.4,30.4], fill=(26,18,8,200), outline=(138,31,26,255), width=2)
    d.line([20.2,27.8,27.8,20.2], fill=(138,31,26,255), width=2)
    out["--cur-no"] = (no, "not-allowed")

    wait = to_size(arrow, 24); d = ImageDraw.Draw(wait)
    d.ellipse([17.4,17.4,30.6,30.6], fill=(26,18,8,205), outline=(138,106,42,255), width=2)
    d.line([20.9,20.2,27.1,20.2], fill=(210,171,85,255), width=2)
    d.line([20.9,27.8,27.1,27.8], fill=(210,171,85,255), width=2)
    d.polygon([(21.7,20.9),(26.3,20.9),(24,24)], fill=(232,201,135,255))
    d.polygon([(21.7,27.1),(26.3,27.1),(24,24)], fill=(138,106,42,255))
    out["--cur-wait"] = (wait, "progress")

    css = io.open(CSS, encoding="utf-8", newline="").read()   # newline='' = 줄바꿈 원형 보존
    for var, (im, kw) in out.items():
        buf = io.BytesIO(); im.save(buf, "PNG")
        b64 = base64.b64encode(buf.getvalue()).decode()
        new = f'{var}:url("data:image/png;base64,{b64}") 0 0, {kw};'
        pat = re.compile(re.escape(var)+r':url\("data:image/(?:png;base64|svg\+xml),.*?"\)\s*[\d ]+,\s*'+kw+r';', re.S)
        css, n = pat.subn(lambda m: new, css, count=1)
        print(f"{var}: {'교체' if n else '못 찾음!'} ({len(b64)}자)")
    io.open(CSS, "w", encoding="utf-8", newline="").write(css)
    print("theme.css 갱신 완료 — Debug·Release 둘 다 빌드할 것")

if __name__ == "__main__":
    build()
