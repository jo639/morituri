"""베이크 산출물 검증([15]§10.8 B3) — 눈이 아니라 수로 판정한다.

    python verify_bake.py

이 검증이 없었으면 못 잡았을 결함 2건(실제로 이걸로 잡았다):
  1. 노멀맵이 sRGB 전달함수를 먹어 x=0이어야 할 바닥이 +0.468로 나왔다.
     단일 픽셀만 봤으면 "모래 요철이겠지"로 넘어갔을 값이다 —
     **영역 평균이 상수 오프셋을 드러냈다.**
  2. Blender 카메라 공간의 y·z 부호가 [15]§3.1과 반대였다.
"""
import math
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
W_LOGICAL, H_LOGICAL, BGS = 940, 560, 2
ARENA_R, RX_PX = 12.0, 400.0


def mean_normal(px, x0, y0, x1, y1, step=3):
    sx = sy = sz = 0.0
    c = 0
    for y in range(y0, y1, step):
        for x in range(x0, x1, step):
            r, g, b = px[x, y][:3]
            sx += r / 127.5 - 1.0
            sy += g / 127.5 - 1.0
            sz += b / 127.5 - 1.0
            c += 1
    return sx / c, sy / c, sz / c


def check(name, got, want, tol):
    ok = abs(got - want) <= tol
    print("    %-22s %+.3f  (기대 %+.3f, 허용 %.2f)  %s"
          % (name, got, want, tol, "OK" if ok else "FAIL"))
    return ok


def main():
    fails = 0
    for tag, elev in (("basic", 20.0), ("zoom", 15.0)):
        alb = os.path.join(HERE, "arena_%s.png" % tag)
        nrm = os.path.join(HERE, "arena_%s_n.png" % tag)
        print("\n[%s] 부각 %.0f도" % (tag, elev))
        for f in (alb, nrm):
            if not os.path.exists(f):
                print("    없음:", f)
                fails += 1
        if fails:
            continue

        for f in (alb, nrm):
            im = Image.open(f)
            want = (W_LOGICAL * BGS, H_LOGICAL * BGS)
            ok = im.size == want
            print("    %-22s %s  (기대 %s)  %s"
                  % (os.path.basename(f), im.size, want, "OK" if ok else "FAIL"))
            fails += 0 if ok else 1

        n = Image.open(nrm).convert("RGB")
        WW, HH = n.size
        px = n.load()
        # 아레나 바닥 중앙 — 모래 요철을 평균으로 지우면 평면 법선이 남는다
        fx, fy, fz = mean_normal(px, int(WW * 0.38), int(HH * 0.50),
                                 int(WW * 0.62), int(HH * 0.62))
        e = math.radians(elev)
        # [15]§3.1 규약(+x 오른쪽 · +y 아래 · +z 화면 밖)에서 바닥면 법선
        fails += 0 if check("바닥 N.x", fx, 0.0, 0.06) else 1
        fails += 0 if check("바닥 N.y", fy, -math.cos(e), 0.06) else 1
        fails += 0 if check("바닥 N.z", fz, math.sin(e), 0.06) else 1
        ln = math.sqrt(fx * fx + fy * fy + fz * fz)
        fails += 0 if check("법선 길이", ln, 1.0, 0.05) else 1

    print("\n총 실패 %d건" % fails)
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
