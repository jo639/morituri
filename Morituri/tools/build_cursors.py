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
STEP   = 14                          # 이웃 간 허용 색차(그라데이션 추적)
GLOBAL = 46                          # 테두리에서 뽑은 배경색과의 최대 허용 색차

def cut_background(im):
    """배경을 알파 0으로.

    이웃 색차만 보면 안 된다: 피사체 경계의 안티에일리어싱이 매끄러운 램프라
    영역성장이 그 램프를 타고 피사체 안으로 걸어 들어간다(창끝 날 가장자리가
    점선처럼 파이고 소켓이 뜯겼다 — 실측 내부 구멍 1222화소).
    그래서 두 조건을 모두 요구한다.
      (1) 이웃과 비슷할 것            → 배경 그라데이션을 따라간다
      (2) 테두리 배경색 표본과도 가까울 것 → 피사체로 넘어가지 못한다
    """
    im = im.convert("RGBA"); w, h = im.size; px = im.load()
    # 테두리를 훑어 배경색 표본을 모은다(그라데이션이라 여러 개 필요)
    samples = set()
    for x in range(0, w, 2):
        samples.add(px[x, 0][:3]); samples.add(px[x, h-1][:3])
    for y in range(0, h, 2):
        samples.add(px[0, y][:3]); samples.add(px[w-1, y][:3])
    samples = list(samples)

    def near_bg(c):
        return any(abs(c[0]-s[0])+abs(c[1]-s[1])+abs(c[2]-s[2]) <= GLOBAL for s in samples)

    bg = [[False]*w for _ in range(h)]; q = deque()
    def seed(x, y):
        if not bg[y][x] and near_bg(px[x, y][:3]):
            bg[y][x] = True; q.append((x, y))
    for x in range(w): seed(x, 0); seed(x, h-1)
    for y in range(h): seed(0, y); seed(w-1, y)
    while q:
        x, y = q.popleft(); r0, g0, b0, _ = px[x, y]
        for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
            nx, ny = x+dx, y+dy
            if 0 <= nx < w and 0 <= ny < h and not bg[ny][nx]:
                c = px[nx, ny][:3]
                if abs(c[0]-r0)+abs(c[1]-g0)+abs(c[2]-b0) <= STEP*3 and near_bg(c):
                    bg[ny][nx] = True; q.append((nx, ny))

    # 남은 구멍 메우기: 바깥(테두리 연결)이 아닌 배경 화소는 피사체 내부 구멍이다.
    outside = [[False]*w for _ in range(h)]; q = deque()
    for x in range(w):
        for y in (0, h-1):
            if bg[y][x] and not outside[y][x]: outside[y][x] = True; q.append((x, y))
    for y in range(h):
        for x in (0, w-1):
            if bg[y][x] and not outside[y][x]: outside[y][x] = True; q.append((x, y))
    while q:
        x, y = q.popleft()
        for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
            nx, ny = x+dx, y+dy
            if 0 <= nx < w and 0 <= ny < h and bg[ny][nx] and not outside[ny][nx]:
                outside[ny][nx] = True; q.append((nx, ny))

    filled = 0
    for y in range(h):
        for x in range(w):
            if bg[y][x] and not outside[y][x]:
                bg[y][x] = False; filled += 1
    for y in range(h):
        for x in range(w):
            if bg[y][x]:
                r, g, b, _ = px[x, y]; px[x, y] = (r, g, b, 0)
    if filled: print(f"   내부 구멍 {filled}화소 메움")
    return im

def interior_holes(im):
    """자가 점검: 각 행의 피사체 구간 안에 뚫린 투명 화소 수(0이어야 정상).

    주의: 덩어리가 둘 이상인 그림(창끝 + 배지)에는 쓰지 말 것. 두 덩어리
    사이의 정상적인 빈 공간을 구멍으로 세어 거짓 경고가 난다."""
    w, h = im.size; px = im.load(); n = 0
    for y in range(h):
        xs = [x for x in range(w) if px[x, y][3] > 0]
        if len(xs) < 2: continue
        n += sum(1 for x in range(min(xs), max(xs)+1) if px[x, y][3] == 0)
    return n

def hotspot(im):
    """뾰족한 끝 = 불투명 화소가 있는 최상단 행의 가운데.
    회전각을 바꾸면 끝 위치도 움직이므로 하드코딩하지 않고 매번 계산한다."""
    w, h = im.size; px = im.load()
    for y in range(h):
        xs = [x for x in range(w) if px[x, y][3] > 140]
        if xs: return (min(xs)+max(xs))//2, y
    return 0, 0

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
    arrow = cut_background(Image.open(SRC/"cursor_arrow.png")).rotate(0, Image.BICUBIC, expand=True)
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
        # 배지가 붙은 금지·대기는 덩어리가 둘이라 구멍 검사가 거짓 경고를 낸다
        holes = interior_holes(im) if var in ("--cur-arrow", "--cur-hand") else 0
        hx, hy = hotspot(im)
        buf = io.BytesIO(); im.save(buf, "PNG")
        b64 = base64.b64encode(buf.getvalue()).decode()
        new = f'{var}:url("data:image/png;base64,{b64}") {hx} {hy}, {kw};'
        pat = re.compile(re.escape(var)+r':url\("data:image/(?:png;base64|svg\+xml),.*?"\)\s*[\d ]+,\s*'+kw+r';', re.S)
        css, n = pat.subn(lambda m: new, css, count=1)
        warn = f"  [경고] 내부 구멍 {holes}화소" if holes else ""
        print(f"{var}: {'교체' if n else '못 찾음!'}  hotspot {hx},{hy}  ({len(b64)}자){warn}")
    io.open(CSS, "w", encoding="utf-8", newline="").write(css)
    print("theme.css 갱신 완료 — Debug·Release 둘 다 빌드해야 실제로 적용된다")

if __name__ == "__main__":
    build()
