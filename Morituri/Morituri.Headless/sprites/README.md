# MORITURI 스프라이트 규격 (M4-b)

뷰어(`viewer.html`)는 이 폴더에 `sprites.json` + 시트 PNG가 있으면 실루엣 대신 그림을 그린다.
없으면 조용히 실루엣 폴백. → **실제 그림은 이 규격만 맞추면 드롭인 교체.**

## 필요한 9포즈 (1v1 전투용)
횡스크롤 튜토리얼의 "걷기/점프"가 아니라, MORITURI 전투 상태에 맞춘 포즈다.
**강공은 예비(텔레그래프)와 타격이 갈린다** — 상대가 그걸 보고 반응하는 수싸움이 핵심이라 분리.

| # | 포즈 | 매핑되는 전투 상태 | 그림 |
|---|---|---|---|
| 1 | `idle`         | Idle | 대기 자세 |
| 2 | `walk`         | Move, Dodge | 전진/이동 |
| 3 | `guard`        | Guard | 방패 들어 막기 |
| 4 | `light_attack` | Windup·Active·Recovery (약공) | 짧고 빠른 찌르기 |
| 5 | `heavy_windup` | Windup (강공) | 무기 크게 당긴 텔레그래프(힘 모음) |
| 6 | `heavy_attack` | Active·Recovery (강공) | 전력 런지(완전히 뻗음) |
| 7 | `hurt`         | HitStun, Stagger | 피격·휘청 |
| 8 | `down`         | Down, GetUp | 넘어짐 |
| 9 | `taunt`        | Taunt | 도발(무기 치켜듦/손짓) |

## 시트 규격
- **방향:** 전부 **측면(옆모습) · 오른쪽 향함**. 왼쪽은 코드가 좌우 반전.
  (게임은 검투사를 옆에서 본다 — 앞/뒤 턴어라운드는 디자인용, 액션 시트는 측면 프로필.)
- **앵커:** 발이 프레임 **하단**에 닿게(바닥 기준). 화면 키 ≈ 78px로 스케일됨.
- **배경:** 투명(remove.bg 후).
- 기본 레이아웃: 96×96 한 칸, **가로 1줄 × 9칸**(= 864×96). 순서 = 위 표 순서.

## 파이프라인 — **3D 베이크 (2026-07-31 확정)**

손그림(나노바나나 → remove.bg → Leshy) 경로는 **폐기**. 이제 3D를 정사영으로 렌더해 픽셀화한다
(Crimson Capes 방식). 무기가 8종이라 무기별 시트를 손으로 그리면 노가다 8회지만,
3D는 무기 모델만 갈아끼우고 배치 1회면 끝난다 — 그게 이 경로를 고른 이유다.

도구: `Morituri/tools/spritebake/`
- `inspect_fbx.py` — 굽기 전 점검(메시 유무 · 액션 구간 · 루트 모션)
- `bake.py` — FBX → 시트 PNG + `animations` 조각 JSON
- `merge_anim.py` — 조각을 `sprites.json`에 병합(타임스탬프 백업)

```bash
blender --background --python bake.py -- \
  --char "source/.../Paladin.fbx" --fbx "source/.../sword and shield walk.fbx" \
  --anim walk_fwd --frames 24 --height 96 --rim 14 --contrast 2.0 --lines 1.0 \
  --supersample 1 --elevation 20
```

### 확정 룩 (이 값으로 전 포즈를 굽는다)

| 옵션 | 값 | 이유 |
|---|---|---|
| `--height` | **96** | 화면 120px·`PX=2`·줌1.6에서 원본과 1:1 ([15]§2.4) |
| `--elevation` | **20** | `asin(CamTilt 0.34)`. **뷰어 부각과 한 세트** |
| `--rim` | **14** | 어두운 아레나에서 실루엣 분리. 평면 먹선은 배경보다 어두워 역효과 |
| `--contrast` | **2.0** | 조명비. 후처리 대비와 달리 입체감이 남는다 |
| `--lines` | **1.0** | Freestyle 윤곽선 |
| `--supersample` | **1** | 라니스타 선택(하한 1px 그대로의 굵은 선) |
| `--toon` | **안 씀** | 갑옷 금속감을 버리는 교환이라 기각 |
| 프레임 | 걷기 **24** | 저해상에서 매끄러움을 만드는 건 픽셀 수가 아니라 프레임 수 |

### 굽다 물리는 함정 (전부 실측으로 물렸다)

1. **FBX 임포트 후 씬 프레임 범위가 액션 범위와 다르다**(1~250 기본 vs 실제 1~34). 씬 범위로 샘플링하면 **에러 없이** 정지 포즈만 반복해 찍힌다. `bake.py`는 액션에서 직접 읽는다.
2. **Mixamo 애니 팩은 Without Skin**(메시 0개). `--char`로 캐릭터 리그에 액션을 얹는다.
3. **팩 애니는 In Place가 아니다.** 루트 이동만큼 카메라를 같이 옮겨 해결(F커브 수정보다 안전 — 본 로컬축을 몰라도 되고 수직 바운스가 보존된다).
4. **태양 하나면 새까맣다.** 확산 = 알베도×조도/π라 갑옷 알베도로는 선형 0.1. 키+필+앰비 3단 필요.
5. **Freestyle 크리스 선을 켜면 안 된다.** 8000버텍스 캐릭터의 내부 모서리마다 선이 그어져 저해상에선 형체가 갈색 진흙이 된다. 실루엣·경계만.
6. **선 색은 순수 검정.** 어두운 선형값(0.04)도 노출·sRGB 변환에 들려 갈색으로 뜬다.
7. **엔진은 Cycles CPU 고정.** Blender 5.2엔 `BLENDER_EEVEE_NEXT`가 없고, EEVEE headless가 Intel 드라이버를 종료 시 죽였다.

### Mixamo 다운로드 설정

`In Place` 체크 · Format `FBX Binary(.fbx)` · Skin `With Skin` · 30 fps · Keyframe Reduction **none**.
원본 FBX는 라이선스·용량(28 MB) 때문에 `.gitignore` 처리 — 산출 시트만 추적한다.

## 현재 파일
- `bake/*.png` + `sprites.json` — 3D 베이크 산출물. `walk_fwd`만 교체 완료, **나머지 8포즈는 아직 옛 손그림 시트**.
- `gladiator*.png`, `chang*/mang*` — 옛 손그림/플레이스홀더. 포즈 배치가 끝나면 정리 대상.
- `sprites_e15.json` · `sprites_e11.json` · `sprites_ss3.json` — 룩 비교용 대안 시트(`?sheet=`로 전환).

## 확장 (나중)
- 무기별 시트: `sprites.json`을 무기 ID별로 분기(`WPN_SWORD.json` 등) → 무기 3D 모델만 교체해 재렌더.
- 8방향: 3D 베이크는 카메라만 돌리면 되므로 싸다. 다만 뷰어가 현재 좌우 flip 2방향만 지원.
