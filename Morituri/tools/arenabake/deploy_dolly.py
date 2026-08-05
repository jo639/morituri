"""돌리 프레임 배포 — PNG를 JPEG로 줄여 뷰어 폴더에 넣는다([15]§10.17).

    python deploy_dolly.py

PNG 24장은 30 MB다. 배경은 사진이라 JPEG가 맞고, 품질 88이면 장당 ~127 KB로 떨어진다.
알파가 없으므로(배경은 화면을 꽉 채운다) 무손실이 필요 없다.
"""
import io, os, glob, shutil
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
DEST = os.path.abspath(os.path.join(HERE, "..", "..", "Morituri.Headless"))
QUALITY = 88

src = sorted(glob.glob(os.path.join(HERE, "dolly_*.png")))
if not src:
    raise SystemExit("dolly_*.png 이 없다 — 베이크부터 돌릴 것")

# 새 시퀀스가 이전보다 짧으면 남은 옛 프레임이 섞인다. 먼저 지운다.
for old in glob.glob(os.path.join(DEST, "dolly_*.jpg")):
    os.remove(old)

total, size = 0, (0, 0)
for p in src:
    im = Image.open(p).convert("RGB")
    size = im.size
    out = os.path.join(DEST, os.path.basename(p)[:-4] + ".jpg")
    im.save(out, "JPEG", quality=QUALITY, optimize=True, subsampling=1)
    total += os.path.getsize(out)

print("%d장 · %dx%d · %.1f MB (장당 %d KB)"
      % (len(src), size[0], size[1], total / 1048576.0, total / len(src) / 1024))
