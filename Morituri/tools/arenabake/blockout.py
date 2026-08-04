"""
아레나 블록아웃([15]§10.8 B1) — 회색 상자만으로 구도·값 구조를 확인한다.

Blender 안에서 돈다:
  blender --background --python blockout.py -- --out blockout.png

설계 메모 (전부 [15]에서 역산한 값이다 — 임의로 고치지 말 것):
- 카메라는 **정사영**이고 뷰어의 피트 투영과 1:1이다.
    px/m      = RX / ArenaRadius = 400 / 12 = 33.333
    ortho 폭  = W / (px/m) = 940 / 33.333 = 28.2 m
    부각 20°  → 반지름 12 m 원이 세로 12·sin20° = 4.10 m = 136.8 px 로 눌린다 (= 뷰어 RY 136) ✓
- 월드축: +X = 화면 오른쪽, +Y = 화면 안쪽(먼 쪽), +Z = 위. 카메라는 −Y 쪽에서 +Y를 본다.
- 태양은 [15]§3.3 확정치 az 233° · el 41°(화면 접선공간). 바닥면 기준으로 월드에 옮기면
  L_world = (−0.45, +0.60, +0.66) — 화면 아래(+y)가 월드 −Y라 y부호만 뒤집힌다.
- **차양(velarium)은 프레임 밖에 둔다.** 라니스타 지시: 차양 자체는 안 보이고 그림자만 보인다.
  정사영이라 화면 위쪽 밖 물체는 렌더에 안 잡히지만 그림자는 그대로 진다.
  높이 h에 있는 물체의 그림자는 (L_xy/L_z)·h 만큼 밀리므로, 아레나에 그림자를 떨어뜨리려면
  차양을 그 반대로 미리 옮겨 둬야 한다(아래 VELA_OFF).
"""
import sys, os, math

import bpy
from mathutils import Vector

# ── [15] 계약값 ───────────────────────────────────────────────
ARENA_R   = 12.0          # Sim ArenaRadius (§1) — 이 원 안에는 아무것도 세우지 않는다
PX_PER_M  = 400.0 / 12.0
ORTHO_W   = 940.0 / PX_PER_M      # 28.2 m
ASPECT    = 940.0 / 528.0   # [15]§2.4 — 16:9(1.780). 세로 528은 PX=2로도 정확히 나뉜다
ELEV      = 20.0                  # 기본 부각 (§2.4.1). 줌 벌은 15°
SUN_AZ, SUN_EL = 233.0, 41.0      # §3.3


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument("--out", default="blockout.png")
    p.add_argument("--res", type=int, default=940)
    p.add_argument("--elevation", type=float, default=ELEV)
    p.add_argument("--cam-height", type=float, default=0.62,
                   help="뷰어 CamHeight와 같은 값. 아레나를 아래로 내려 하단 여백을 줄인다")
    p.add_argument("--samples", type=int, default=48)
    p.add_argument("--exposure", type=float, default=-0.35, help="Standard 변환은 하이라이트가 바로 탄다")
    p.add_argument("--values", action="store_true",
                   help="§10.3 값 3단을 재질에 반영(기본: 전부 균일 회색 = 순수 블록아웃)")
    p.add_argument("--wall-h", type=float, default=3.5, help="포디움 벽 높이(m)")
    p.add_argument("--vela", type=float, default=1.0, help="차양 그림자 세기 0~1 (0=차양 제거)")
    p.add_argument("--no-velarium", action="store_true")
    p.add_argument("--no-fg", action="store_true", help="앞쪽 관중석 실루엣 제거")
    p.add_argument("--fg-h", type=float, default=1.0,
                   help="앞쪽 실루엣 높이 배율. 화면 하단을 덜 가리게 낮춘다")
    p.add_argument("--b2", action="store_true", help="B2 재질 팔레트(§10.5) 적용")
    p.add_argument("--detail", action="store_true", help="벽 석재 줄눈 + 모래 절차 요철(B2)")
    p.add_argument("--attic", action="store_true", help="아케이드/상단 관중석 — 이 카메라에선 프레임 밖(§10.11)")
    p.add_argument("--back-squash", type=float, default=1.00, help="뒤쪽 구조물 압축(1=압축 없음)")
    p.add_argument("--veg", action="store_true", help="식생 — 기본 꺼짐(라니스타 지시로 제거)")
    p.add_argument("--normal", action="store_true", help="노멀맵 패스도 함께 출력(B3)")
    p.add_argument("--ruin", type=float, default=0.22, help="무너진 윗선 진폭(0=평평)")
    p.add_argument("--cut-keep", type=float, default=65.0, help="앞쪽 컷어웨이 각반경(도)")
    p.add_argument("--cut-fade", type=float, default=35.0, help="컷어웨이 페이드 폭(도)")
    # ── 돌리 시퀀스([15]§10.17) — 줌 경로를 따라 N장을 실제로 렌더한다 ──────────
    # 줌은 **정해진 1차원 경로**다(zoomFrac 0→1). 그 위의 프레임을 미리 구워 두면
    # 부각이 연속으로 낮아지고 크로스페이드 이중상이 사라진다. 런타임 3D는 0.
    p.add_argument("--dolly", type=int, default=0, help="돌리 프레임 수(0=끔). 24 권장")
    p.add_argument("--overscan-y", type=float, default=1.0,
                   help="돌리 프레임 세로 여유. 뷰어가 camLift(줌 46 · 인트로 100 px)만큼 "
                        "배경을 내리므로 그만큼 위가 비어 보인다 → (528+2·100)/528 = 1.42")
    p.add_argument("--overscan", type=float, default=1.0,
                   help="돌리 프레임 가로 여유. 카메라가 선수를 추적하며 팬하면 "
                        "1:1 프레임은 가장자리가 비어 잘린다 — camZoom 1.6에서 팬 최대 218 px, "
                        "940+2·218 = 1376 → 배율 1.5가 필요하다. 세로는 팬이 0이라 불필요")
    p.add_argument("--zoom-follow", type=float, default=1.6, help="뷰어 ZoomFollow와 같은 값")
    p.add_argument("--tilt-basic", type=float, default=0.34)
    p.add_argument("--tilt-zoom", type=float, default=0.26)
    return p.parse_args(argv)


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mat(name, value, rough=0.85):
    """value = 스칼라(회색) 또는 (r,g,b)."""
    if not isinstance(value, (tuple, list)):
        value = (value, value, value)
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    bsdf = m.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (value[0], value[1], value[2], 1)
    bsdf.inputs["Roughness"].default_value = rough
    return m


def mesh_from(name, verts, faces, material):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.validate()
    me.update()
    ob = bpy.data.objects.new(name, me)
    ob.data.materials.append(material)
    bpy.context.collection.objects.link(ob)
    return ob


def lathe(name, profile, material, seg=192, gaps=(), zscale=None):
    """(r,z) 폴리라인을 Z축으로 돌려 띠 표면을 만든다.
    gaps   = [(a0,a1), ...] (도) 구간은 건너뛴다 → 철문 같은 구멍
    zscale = f(각도)→0~1. 각도마다 높이를 눌러 **앞쪽 컷어웨이**를 만든다(§10.9 결정 3).
             0이면 그 각도는 통째로 생략한다."""
    verts, faces = [], []

    def in_gap(a):
        a %= 360.0
        for g0, g1 in gaps:
            if g0 <= a <= g1 or (g0 > g1 and (a >= g0 or a <= g1)):
                return True
        return False

    step = 360.0 / seg
    for i in range(seg):
        a0, a1 = i * step, (i + 1) * step
        am = (a0 + a1) * 0.5
        if in_gap(am):
            continue
        k0 = 1.0 if zscale is None else zscale(a0)
        k1 = 1.0 if zscale is None else zscale(a1)
        if max(k0, k1) <= 0.001:
            continue
        c0, s0 = math.cos(math.radians(a0)), math.sin(math.radians(a0))
        c1, s1 = math.cos(math.radians(a1)), math.sin(math.radians(a1))
        for (r0, z0), (r1, z1) in zip(profile[:-1], profile[1:]):
            b = len(verts)
            verts += [(r0 * c0, r0 * s0, z0 * k0), (r0 * c1, r0 * s1, z0 * k1),
                      (r1 * c1, r1 * s1, z1 * k1), (r1 * c0, r1 * s0, z1 * k0)]
            faces.append((b, b + 1, b + 2, b + 3))
    return mesh_from(name, verts, faces, material)


def cutaway(near_deg=270.0, keep=110.0, fade=45.0, floor=0.0):
    """앞쪽(카메라 쪽) 구조물을 낮춘다.
    부각 20°에서 높이 h 인 앞벽은 그 뒤 h/tan20° = 2.75h 만큼의 바닥을 가린다 —
    3.5 m 벽이면 9.6 m, 지름 24 m 아레나의 40%다. 결정 3(남단 발밑 침범 금지)을 지키려면
    앞쪽은 세울 수 없다. 실물 경기장을 자르는 게 아니라 **카메라 쪽 벽만 걷어내는** 연출 관례."""
    def f(a):
        d = abs(((a - near_deg + 180.0) % 360.0) - 180.0)   # 앞쪽 중심에서의 각거리
        if d >= keep + fade:
            return 1.0
        if d <= keep:
            return floor
        return floor + (1.0 - floor) * (d - keep) / fade
    return f


def stone_material(name, base, scale=6.0, bevel=0.035, grime=0.55, rough=0.82, streak=0.0):
    """풍화된 석재. **블록아웃과 사진을 가르는 것은 색이 아니라 이 세 가지다:**

      ① 모서리 마모 — 실제 돌은 모서리가 깨져 둥글다. 각진 상자는 그 자체로 '3D 블록아웃'이다.
         `Bevel` 노드는 지오메트리 없이 셰이딩 단계에서 모서리를 굴린다(폴리곤 비용 0).
      ② 틈의 때 — 때는 **오목한 곳에 쌓인다**. AO를 마스크로 써서 오목한 곳만 어둡게 하면
         '더러워 보이는' 게 아니라 '오래돼 보인다'. 이 한 수가 가장 크다.
      ③ 돌마다 다른 색 — Voronoi 셀 난수로 블록별 톤을 흔든다. 균일한 색은 페인트지 석재가 아니다.

    세 신호를 스칼라 하나로 합쳐 ColorRamp에 태운다(Mix 노드 버전 차이를 피한다)."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]

    vor = nt.nodes.new("ShaderNodeTexVoronoi")          # ③ 블록별 난수
    vor.inputs["Scale"].default_value = scale
    bw = nt.nodes.new("ShaderNodeRGBToBW")
    nt.links.new(vor.outputs["Color"], bw.inputs["Color"])

    nz = nt.nodes.new("ShaderNodeTexNoise")             # 얼룩·풍화
    nz.inputs["Scale"].default_value = scale * 2.5
    nz.inputs["Detail"].default_value = 8.0

    ao = nt.nodes.new("ShaderNodeAmbientOcclusion")     # ② 틈의 때
    ao.inputs["Distance"].default_value = 0.45

    def mul(src, out, k):
        n = nt.nodes.new("ShaderNodeMath"); n.operation = "MULTIPLY"
        n.inputs[1].default_value = k
        nt.links.new(src.outputs[out], n.inputs[0])
        return n

    def add(a, b):
        n = nt.nodes.new("ShaderNodeMath"); n.operation = "ADD"
        nt.links.new(a.outputs[0], n.inputs[0]); nt.links.new(b.outputs[0], n.inputs[1])
        return n

    s = add(add(mul(bw, "Val", 0.42), mul(nz, "Fac", 0.24)), mul(ao, "AO", grime))
    if streak > 0.0:
        # 흘러내린 자국 — 노이즈를 세로로 길게 늘여 **아래로 흐른 얼룩**을 만든다.
        # 규칙적인 무늬가 아니라 '무언가 흘렀다'는 흔적이라 서사가 붙는다(핏자국·빗물).
        tc = nt.nodes.new("ShaderNodeTexCoord")
        mp = nt.nodes.new("ShaderNodeMapping")
        mp.inputs["Scale"].default_value = (5.0, 5.0, 0.22)   # z만 눌러 세로로 늘인다
        nt.links.new(tc.outputs["Object"], mp.inputs["Vector"])
        st = nt.nodes.new("ShaderNodeTexNoise")
        st.inputs["Scale"].default_value = 3.0
        st.inputs["Detail"].default_value = 5.0
        nt.links.new(mp.outputs["Vector"], st.inputs["Vector"])
        sub = nt.nodes.new("ShaderNodeMath"); sub.operation = "SUBTRACT"
        nt.links.new(s.outputs[0], sub.inputs[0])
        nt.links.new(mul(st, "Fac", streak).outputs[0], sub.inputs[1])
        s = sub

    ramp = nt.nodes.new("ShaderNodeValToRGB")
    r, g, b = base
    ramp.color_ramp.elements[0].position = 0.15
    ramp.color_ramp.elements[0].color = (r * 0.34, g * 0.33, b * 0.30, 1)   # 틈의 때
    ramp.color_ramp.elements[1].position = 0.95
    ramp.color_ramp.elements[1].color = (min(1, r * 1.22), min(1, g * 1.20), min(1, b * 1.14), 1)
    ramp.color_ramp.elements.new(0.38).color = (r * 0.74, g * 0.76, b * 0.80, 1)   # 회색기
    ramp.color_ramp.elements.new(0.58).color = (r, g, b, 1)
    ramp.color_ramp.elements.new(0.78).color = (min(1, r * 1.12), g * 1.02, b * 0.86, 1)  # 황토기
    nt.links.new(s.outputs[0], ramp.inputs["Fac"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = rough

    bev = nt.nodes.new("ShaderNodeBevel")               # ① 모서리 마모
    bev.inputs["Radius"].default_value = bevel
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.35
    bump.inputs["Distance"].default_value = 0.02
    nt.links.new(nz.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bev.outputs["Normal"], bump.inputs["Normal"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def sand_bump(m, rake=0.70, grain=0.30, dist=0.03):
    """모래 요철을 **절차 텍스처**로 준다. 지오메트리로 깎으면 폴리곤이 폭발하고,
    어차피 B3에서 노멀맵으로 구워질 것이라 여기서 셰이더로 넣는 게 맞다.
    rake = 동심 갈퀴 자국(Wave RINGS) · grain = 모래알(Noise)."""
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    wave = nt.nodes.new("ShaderNodeTexWave")
    wave.wave_type = "RINGS"
    wave.inputs["Scale"].default_value = 5.5
    wave.inputs["Distortion"].default_value = 3.4
    wave.inputs["Detail"].default_value = 2.0
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 60.0
    noise.inputs["Detail"].default_value = 6.0
    mr = nt.nodes.new("ShaderNodeMath"); mr.operation = "MULTIPLY"; mr.inputs[1].default_value = rake
    mg = nt.nodes.new("ShaderNodeMath"); mg.operation = "MULTIPLY"; mg.inputs[1].default_value = grain
    add = nt.nodes.new("ShaderNodeMath"); add.operation = "ADD"
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 1.0
    bump.inputs["Distance"].default_value = dist * 5.0
    nt.links.new(wave.outputs["Fac"], mr.inputs[0])
    nt.links.new(noise.outputs["Fac"], mg.inputs[0])
    nt.links.new(mr.outputs[0], add.inputs[0])
    nt.links.new(mg.outputs[0], add.inputs[1])
    nt.links.new(add.outputs[0], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def wall_courses(name, r, z0, z1, mat_stone, mat_joint, courses=6, n=90, zscale=None, gaps=()):
    """포디움 벽 석재 — 로마 오푸스 콰드라툼(다듬은 큰 돌 쌓기).

    **안쪽으로는 한 톨도 안 나온다** — r=12는 Sim 충돌 경계다(§1). 벽면 본체를 바깥으로 물리고
    굽도리·처마만 r에 둬서 도드라지게 한다.

    초판이 부자연스러웠던 이유는 돌 크기가 아니라 **기계로 쌓은 듯한 균일함**이었다. 셋을 고쳤다:
      ① **단 높이를 단마다 다르게** — 실물은 층마다 돌 크기가 다르다. 같은 높이가 정확히
         반복되면 눈이 돌보다 격자를 먼저 본다.
      ② **빠진 돌 16% → 4%.** 그렇게 많으면 세월이 아니라 **이가 빠진 것**으로 보인다.
         대신 **모든 돌에 ±1.5 cm 미세 요철** — 면이 고르지 않은 것이 진짜 석재의 신호다.
      ③ **줄눈을 얇고 옅게** — 두껍고 검으면 석조가 아니라 격자무늬가 된다."""
    PLINTH, CORNICE = 0.42, 0.30
    RB = r + 0.06
    zb0, zb1 = z0 + PLINTH, z1 - CORNICE

    lathe(name + "_joint", [(RB + 0.028, zb0), (RB + 0.028, zb1)], mat_joint, zscale=zscale, gaps=gaps)
    lathe(name + "_plinth", [(r, z0), (r, zb0), (RB, zb0)], mat_stone, zscale=zscale, gaps=gaps)
    lathe(name + "_cornice", [(RB, zb1), (r, zb1 + 0.07), (r, z1)], mat_stone, zscale=zscale, gaps=gaps)

    sd = 987

    def rnd():
        nonlocal sd
        sd = (sd * 1103515245 + 12345) & 0x7FFFFFFF
        return (sd % 10000) / 10000.0

    ws = [0.80 + rnd() * 0.55 for _ in range(courses)]     # ① 단 높이를 흔들고 정규화
    tot = sum(ws)
    step = 360.0 / n
    zc = zb0
    for c, w in enumerate(ws):
        h = (zb1 - zb0) * w / tot
        za, zz = zc, zc + h * 0.94                          # 위 6% = 가로 줄눈(얇게)
        zc += h
        a = step * (0.5 if c % 2 else 0.0)
        while a < 360.0:
            bw = step * (0.62 + rnd() * 0.95)
            depth = (rnd() - 0.5) * 0.030                   # ② 모든 돌에 미세 요철
            if rnd() < 0.04:
                depth += 0.035 + rnd() * 0.030              # ② 드물게 물러난 돌
            lathe("%s_c%d_%d" % (name, c, int(a * 10)),
                  [(RB + depth, za), (RB + depth, zz)], mat_stone, seg=96, zscale=zscale,
                  gaps=list(gaps) + [((a + bw * 0.94) % 360.0, a % 360.0)])   # ③ 세로 줄눈 얇게
            a += bw


def backsquash(back=90.0, min_k=0.34, width=72.0):
    """뒤쪽 구조물만 낮춘다 — **무대 세트 수법**.

    화면 세로 = 0.342·y + 0.940·z 이고 프레임 위 끝이 +6.60 m다. 아레나 뒤 가장자리가
    이미 4.10 m를 먹으므로 **뒤쪽이 쓸 수 있는 월드 높이는 2.66 m뿐**이다.
    포디움 벽 3.5 m 하나가 그 예산을 다 먹고 넘쳐서 좌석이 들어갈 자리가 없었다.

    카메라를 올리면 해결되지만 `CamTilt`는 스프라이트 베이크와 묶여 있다(§2.4.4 규칙 1).
    한 각도에서만 보는 그림이므로 **뒤쪽만 납작하게** 만든다 — 옆쪽(y≈0)은 화면 여유가
    7 m라 그대로 두고, 뒤쪽만 눌러 좌석이 벽 위로 올라오게 한다."""
    def f(a):
        d = abs(((a - back + 180.0) % 360.0) - 180.0)
        if d >= width:
            return 1.0
        return min_k + (1.0 - min_k) * (d / width)
    return f


def ruin(seed=7, amp=0.22, harmonics=(3, 5, 8, 13, 21)):
    """무너진 실루엣([15]§10.13 #6) — 윗선을 각도에 따라 불규칙하게 낮춘다.

    사진은 **폐허**다. 완벽한 수평선이 하나라도 남아 있으면 도면으로 보인다.
    난수를 그냥 뿌리면 지글거리므로 서로 안 맞아떨어지는 배음(3·5·8·13·21)의 합으로
    **연속이면서 주기가 안 읽히는** 곡선을 만든다."""
    import random
    rng = random.Random(seed)
    ph = [rng.random() * 6.2832 for _ in harmonics]

    def f(a):
        r = math.radians(a)
        v = sum(math.sin(h * r + p) for h, p in zip(harmonics, ph)) / len(harmonics)
        return 1.0 - amp * (0.5 + 0.5 * v)
    return f


def arch_gate(name, r, r_out, span, ga, wh, mat_stone, mat_dark, spring=1.9, rise=1.0, seg=40):
    """아치 통로([15]§10.13 #7) — 납작한 홈이 아니라 **깊이가 있는 구멍**.

    아케이드가 주는 인상의 대부분은 구멍 자체가 아니라 **그 안의 어둠이 얼마나 깊은가**다.
    ① 개구부 윗선을 반원으로(중앙이 가장 높다) ② 그 위로 벽을 채워 아치를 만든다
    ③ 뒤로 물러나는 통로를 어둠으로 깐다."""
    verts, faces = [], []
    for i in range(seg):
        t0 = -1.0 + 2.0 * i / seg
        t1 = -1.0 + 2.0 * (i + 1) / seg
        a0 = ga + span * 0.5 * t0
        a1 = ga + span * 0.5 * t1
        h0 = spring + rise * math.sqrt(max(0.0, 1.0 - t0 * t0))   # 반원 개구부
        h1 = spring + rise * math.sqrt(max(0.0, 1.0 - t1 * t1))
        c0, s0 = math.cos(math.radians(a0)), math.sin(math.radians(a0))
        c1, s1 = math.cos(math.radians(a1)), math.sin(math.radians(a1))
        b = len(verts)
        verts += [(r * c0, r * s0, h0), (r * c1, r * s1, h1),
                  (r * c1, r * s1, wh), (r * c0, r * s0, wh)]     # 아치 위쪽 벽
        faces.append((b, b + 1, b + 2, b + 3))
    mesh_from(name + "_arch", verts, faces, mat_stone)
    # 통로 — 뒤로 물러나는 어둠. 천장은 아치 꼭대기에 맞춘다
    lathe(name + "_tunnel",
          [(r, 0.0), (r, spring + rise), (r_out, spring + rise)], mat_dark,
          gaps=[((ga + span * 0.5) % 360.0, (ga - span * 0.5) % 360.0)])


def combine(*fns):
    """여러 zscale을 곱해 합친다(컷어웨이 × 뒤쪽 압축)."""
    return lambda a: math.prod(f(a) for f in fns)


def periodic_gaps(a0, a1, n, duty=0.5):
    """[a0,a1)을 n등분해 각 칸의 duty 비율을 '건너뛸 구간'으로 만든다.
    톱니(좌석 등받이)·아케이드 기둥처럼 규칙적인 실루엣을 만들 때 쓴다."""
    span = (a1 - a0) % 360.0 or 360.0
    step = span / n
    return [((a0 + i * step) % 360.0, (a0 + i * step + step * duty) % 360.0) for i in range(n)]


def disc(name, r, z, material, seg=192):
    verts = [(0, 0, z)] + [(r * math.cos(math.radians(i * 360.0 / seg)),
                            r * math.sin(math.radians(i * 360.0 / seg)), z) for i in range(seg)]
    faces = [(0, i + 1, (i + 1) % seg + 1) for i in range(seg)]
    return mesh_from(name, verts, faces, material)


def build(a):
    if a.b2:
        # B2 재질 팔레트(§10.5) — 값 3단은 유지하고 색만 입힌다. 재질이 갈려야 형태가 읽힌다.
        v_floor = (0.74, 0.55, 0.35)     # 붉은 모래 — 초점
        v_wall  = (0.50, 0.47, 0.41)     # 트래버틴 — 중간
        v_seat  = (0.42, 0.39, 0.34)     # 관중석 석회석 — 참고 이미지처럼 밝게(열이 읽혀야 한다)
        v_dark  = (0.045, 0.040, 0.038)  # 검은 공동
    else:
        v_floor = 0.62 if a.values else 0.5      # §10.3: 초점(밝게)
        v_wall  = 0.42 if a.values else 0.5      #         중간
        v_seat  = 0.20 if a.values else 0.5      #         프레임(어둡게)
        v_dark  = 0.03

    m_floor = mat("floor", v_floor, 0.95)
    if a.b2:
        m_wall = stone_material("wall", v_wall, scale=7.0, bevel=0.04, streak=0.34)
        m_seat = stone_material("seat", v_seat, scale=14.0, bevel=0.03, grime=0.62)
    else:
        m_wall, m_seat = mat("wall", v_wall, 0.80), mat("seat", v_seat)
    m_dark = mat("dark", v_dark)
    m_joint = mat("joint", tuple(x * 0.80 for x in v_wall) if isinstance(v_wall, tuple)
                  else v_wall * 0.80)

    # 바닥 — Sim 원 그대로. 이 안에는 아무것도 없다(§1 계약)
    if a.detail:
        sand_bump(m_floor)
    disc("floor", ARENA_R, 0.0, m_floor)

    # 철문 2곳: 좌·우(az 0·180) ±7°
    # 철문 2곳 — az 0·180°(정확한 좌우)는 화면 좌·우 **끝**이라 아치가 잘렸다.
    # 뒤로 살짝(35°) 물리면 화면 안으로 들어오면서 좌우 감은 유지된다.
    # 덤: 앞쪽 컷어웨이 페이드 구간(170°~10°)에서도 벗어나 문이 온전한 높이로 선다.
    GATE_AZ = (35.0, 145.0)
    GATES = [(g - 5.0, g + 5.0) for g in GATE_AZ]

    # 포디움 벽 — 아레나를 '구덩이'로 만드는 요소(§10.4 #1).
    # 앞쪽은 컷어웨이: 낮은 턱(lip)만 남긴다 → 남단 검투사를 가리지 않는다(결정 3).
    WH = a.wall_h
    LIP = 0.35 / WH                      # 앞쪽에 남길 높이 비율(≈0.35 m 턱)
    squash = backsquash(min_k=a.back_squash)
    cut = combine(cutaway(keep=a.cut_keep, fade=a.cut_fade, floor=LIP), squash)
    if a.detail:
        # 화면 상단의 **유일한** 요소다(§10.11) — 디테일 예산을 여기 몰아넣는다.
        wall_courses("podium", ARENA_R, 0.0, WH, m_wall, m_joint,
                     courses=6, n=80, zscale=cut, gaps=GATES)
        lathe("podium_cap", [(ARENA_R, WH), (ARENA_R + 0.6, WH)], m_wall, gaps=GATES, zscale=cut)
    else:
        lathe("podium", [(ARENA_R, 0.0), (ARENA_R, WH), (ARENA_R + 0.6, WH)], m_wall,
              gaps=GATES, zscale=cut)
    # 철문 안쪽: 검은 공동(§10.5). gaps는 '건너뛸 구간'이라 문만 남기려면 **여집합**을 준다 —
    # 두 문을 한 번에 처리하려고 합쳐 쓰면 합집합이 전체가 되어 아무것도 안 남는다(실측 후 분리).
    for i, ga in enumerate(GATE_AZ):
        arch_gate("gate%d" % i, ARENA_R + 0.06, ARENA_R + 4.5, 10.0, ga, WH, m_wall, m_dark,
                  spring=1.45, rise=1.05)   # 폭 2.1m x 높이 2.5m — 로마 아치는 폭보다 높다

    # 관중석 3단 — 통로(vomitoria) 4곳으로 끊는다(§10.4 #3). 앞쪽은 같은 컷어웨이로 사라진다.
    # 방사형 계단(vomitoria) — 참고 이미지에서 관중석을 가장 알아보게 만드는 요소.
    # 열이 통째로 이어지면 골판지로 보인다. 쐐기로 끊어야 '구역'이 생긴다.
    VOM = [(a - 2.6, a + 2.6) for a in (25.0, 65.0, 115.0, 155.0, 205.0, 245.0, 295.0, 335.0)]
    # 좌석 열 — 큰 단 3개는 '회색 쐐기'로 보인다. 실제로 보이는 건 좌·우 가장자리뿐이지만(§10.11)
    # 거기서 관중석으로 읽히려면 열이 있어야 한다. 라이저 0.30 · 트레드 0.55 — 참고 이미지의 촘촘한 열
    cavea = [(ARENA_R + 0.6, WH), (ARENA_R + 2.4, WH)]          # 순회 통로
    rr, zz = ARENA_R + 2.4, WH
    for _row in range(22):
        zz += 0.30; cavea.append((rr, zz))                       # 라이저
        rr += 0.55; cavea.append((rr, zz))                       # 트레드
    cut_seat = combine(cutaway(keep=a.cut_keep, fade=a.cut_fade, floor=0.0), squash,
                       ruin(seed=7, amp=a.ruin))          # 폐허 윗선(§10.13 #6)
    lathe("cavea", cavea, m_seat, gaps=VOM, zscale=cut_seat)
    # 계단 바닥 — 관중석보다 어둡게 깔아 쐐기가 실루엣으로 읽히게 한다
    lathe("vom_floor", [(p[0], p[1] - 0.16) for p in cavea], m_dark,
          gaps=[(v[1], VOM[(i + 1) % len(VOM)][0]) for i, v in enumerate(VOM)], zscale=cut_seat)

    # 아케이드 — 기본 OFF. **이 카메라에서는 영영 안 보인다**(§10.11).
    #   화면 세로 = 0.342·y + 0.940·z, 프레임 절반 ±6.60 m
    #   → 보이는 높이 상한 z ≤ (6.60 − 0.342·y) / 0.940
    #   아케이드는 y=+30에 z=15.7이라 상한이 음수다. 옆쪽(y≈0)에서도 상한 7.0 m라 여전히 밖.
    # 세워도 그림자조차 아레나에 안 닿는다(h=15.7의 그림자는 14 m 밖으로 떨어진다).
    if a.attic:
        TOP_R, TOP_Z = ARENA_R + 17.0, WH + 8.0
        lathe("attic_void", [(TOP_R + 1.2, TOP_Z), (TOP_R + 1.2, TOP_Z + 4.2)], m_dark,
              zscale=cut_seat)
        lathe("arcade", [(TOP_R, TOP_Z), (TOP_R, TOP_Z + 4.2)], m_wall,
              gaps=periodic_gaps(0.0, 360.0, n=40, duty=0.52), zscale=cut_seat)

    # 앞쪽 관중석 실루엣 — 컷어웨이로 비워진 화면 하단을 채우는 **프레임 요소**(라니스타 지시).
    # 상한은 취향이 아니라 기하학이다. 화면 세로 = 0.342·y + 0.940·z 이고 아레나 남단 발밑이 −4.10 m다.
    #   반경 r(=−y)에서 허용 높이 z ≤ (0.342·r − 4.40) / 0.940     ← −4.40 = 발밑에서 0.3 m 여유
    #   r=20 → 2.60 m.  단이 r당 0.35씩 오르면 화면 높이가 그대로 유지된다(−0.342 + 0.94·0.35 ≈ 0)
    #   → 위 가장자리가 남단 바로 아래에 **평행하게** 눕는다. 아래로는 프레임 밖까지 내려가 하단을 채운다.
    if not a.no_fg:
        FG_ARC = [(350.0, 190.0)]                              # 앞 아크(190~350°)에만
        fg = [(20.0, 0.0), (20.0, 2.60 * a.fg_h),                       # 뒤판 — 프레임 하단을 메운다
              (21.3, 2.95 * a.fg_h), (22.6, 3.40 * a.fg_h),
              (24.0, 3.90 * a.fg_h), (25.6, 4.45 * a.fg_h)]   # 단 4개
        lathe("fg_cavea", fg, m_dark, gaps=FG_ARC)
        # 윗선을 톱니로 — 매끈한 곡선은 '검은 막대'로 읽힌다. 관객 머리·좌석 등받이의 실루엣.
        # 이 높이(0.37 m ≈ 화면 12 px)까지가 한계다. 위 식에 넣으면 남단 발밑에서 5 px 남는다.
        # 균일한 톱니는 그 자체로 규칙성이 보인다 → 높이를 결정론적으로 흔들고 가끔 기둥을 세운다.
        sd = 12345
        def rnd():
            nonlocal sd
            sd = (sd * 1103515245 + 12345) & 0x7FFFFFFF
            return (sd % 10000) / 10000.0
        N = 46
        span = (350.0 - 190.0)
        for i in range(N):
            a0 = 190.0 + span * i / N
            w = span / N * (0.40 + rnd() * 0.22)
            tall = rnd() < 0.13                                  # 난간 기둥 — 가끔 하나씩
            top = (2.60 + (0.37 if tall else 0.16 + rnd() * 0.17)) * a.fg_h
            verts, faces = [], []
            for (r0, z0), (r1, z1) in [((20.6, 2.60 * a.fg_h), (20.6, top)),
                                       ((20.6, top), (21.2, top))]:
                c0, s0 = math.cos(math.radians(a0)), math.sin(math.radians(a0))
                c1, s1 = math.cos(math.radians(a0 + w)), math.sin(math.radians(a0 + w))
                b = len(verts)
                verts += [(r0 * c0, r0 * s0, z0), (r0 * c1, r0 * s1, z0),
                          (r1 * c1, r1 * s1, z1), (r1 * c0, r1 * s0, z1)]
                faces.append((b, b + 1, b + 2, b + 3))
            mesh_from("fg_tooth%d" % i, verts, faces, m_dark)

    if a.veg:
        # 식생([15]§10.13 #9) — **기본 꺼짐.** 라니스타 지시로 제거했다.
        # 화면에서 몇 픽셀짜리 검은 점으로만 남아 폐허감보다 얼룩으로 읽혔다.
        m_veg = mat("veg", (0.10, 0.14, 0.06), 1.0)
        sd2 = 4242
        for _i in range(26):
            sd2 = (sd2 * 1103515245 + 12345) & 0x7FFFFFFF
            aa = (sd2 % 10000) / 10000.0 * 360.0
            if abs(((aa - 270.0 + 180.0) % 360.0) - 180.0) < 90.0:
                continue                                    # 앞쪽은 컷어웨이라 안 보인다
            rr2 = ARENA_R + 0.9 + ((sd2 >> 7) % 100) / 100.0 * 1.3
            hh = 0.22 + ((sd2 >> 13) % 100) / 100.0 * 0.28
            c, s_ = math.cos(math.radians(aa)), math.sin(math.radians(aa))
            vv, ff = [], []
            for k in range(3):
                d = (k - 1) * 0.13
                vv += [((rr2 + d) * c, (rr2 + d) * s_, WH),
                       ((rr2 + d + 0.09) * c, (rr2 + d + 0.09) * s_, WH),
                       ((rr2 + d + 0.04) * c, (rr2 + d + 0.04) * s_, WH + hh)]
                ff.append((len(vv) - 3, len(vv) - 2, len(vv) - 1))
            mesh_from("veg%d" % _i, vv, ff, m_veg)

    if not a.no_velarium and a.vela > 0:
        # 차양 — 프레임 밖 상공. 보이지 않고 그림자만 남는다(라니스타 지시).
        # 실제 벨라리움은 **관중석 위**에 걸리고 가운데가 뚫려 있다(oculus). 아레나 위가 아니다.
        # 그리고 그림자 위치를 인위로 맞추지 않는다 — 광원이 고도 41°로 비스듬한데 그림자만
        # 한가운데 동그랗게 앉아 있으면 빛과 그림자가 서로 다른 말을 한다.
        # 제자리에 걸면 그림자는 광원 반대쪽(화면 오른쪽·앞쪽)으로 눕는다. 그게 현실적이다.
        Lx, Ly, Lz = sun_vec()
        m_vela = mat("velarium", 0.5)
        bsdf = m_vela.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Alpha"].default_value = 1.0 - 0.42 * a.vela
        verts, faces = [], []
        R_IN, R_OUT, N, DUTY = 9.0, 34.0, 24, 0.62
        Z_IN, Z_OUT = 11.5, 14.5        # 안쪽이 처진 원뿔 — 평평한 판보다 그림자가 자연스럽게 눕는다
        step = 2.0 * math.pi / N
        for i in range(N):
            a0 = i * step
            a1 = a0 + step * DUTY
            b = len(verts)
            for (rr, aa, zz) in ((R_IN, a0, Z_IN), (R_OUT, a0, Z_OUT),
                                 (R_OUT, a1, Z_OUT), (R_IN, a1, Z_IN)):
                verts.append((math.cos(aa) * rr, math.sin(aa) * rr, zz))
            faces.append((b, b + 1, b + 2, b + 3))
        ob = mesh_from("velarium", verts, faces, m_vela)
        ob.visible_camera = False          # 혹시 프레임에 걸려도 안 보이게 (그림자는 유지)


def sun_vec():
    """§3.3 화면 접선공간 L → 월드. 화면 아래(+y)가 월드 −Y이므로 y부호만 뒤집는다."""
    ce = math.cos(math.radians(SUN_EL))
    return (math.cos(math.radians(SUN_AZ)) * ce,
            -math.sin(math.radians(SUN_AZ)) * ce,
            math.sin(math.radians(SUN_EL)))


def setup(a):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    sc.cycles.samples = a.samples
    sc.cycles.use_denoising = True
    sc.render.resolution_x = a.res
    sc.render.resolution_y = int(round(a.res / ASPECT))
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = False
    # 스프라이트 베이크와 같은 컬러 설정 — AgX/Filmic은 색을 눕혀버린다(spritebake bake.py:225).
    # 배경만 다른 변환을 쓰면 캐릭터와 팔레트가 어긋난다. §10.2 어법 계약.
    sc.view_settings.view_transform = "Standard"
    sc.view_settings.exposure = a.exposure
    sc.world = bpy.data.worlds.new("w")
    sc.world.use_nodes = True
    sc.world.node_tree.nodes["Background"].inputs[0].default_value = (0.09, 0.105, 0.135, 1)
    sc.world.node_tree.nodes["Background"].inputs[1].default_value = 0.35

    cam_d = bpy.data.cameras.new("cam")
    cam_d.type = "ORTHO"
    cam_d.ortho_scale = ORTHO_W
    cam = bpy.data.objects.new("cam", cam_d)
    el = math.radians(a.elevation)
    D = 80.0
    cam.location = (0.0, -D * math.cos(el), D * math.sin(el))
    cam.rotation_euler = (math.radians(90.0) - el, 0.0, 0.0)
    # 프레이밍 — 아레나를 화면 아래로 내려 하단 여백을 줄인다([15]§2.4).
    # 카메라를 제 up축으로 올리면 피사체는 화면에서 내려간다.
    #   화면 이동(px) = (CamHeight − 0.5)·H   →   월드 이동(m) = 그 값 / (RX/ArenaRadius)
    # ⚠ **뷰어 CamHeight와 반드시 같은 값**이어야 한다. 한쪽만 바꾸면 배경과 플레이 타원이 어긋난다.
    up = Vector((0.0, math.sin(el), math.cos(el)))
    cam.location = tuple(Vector(cam.location)
                         + up * ((a.cam_height - 0.5) * (940.0 / ASPECT) / PX_PER_M))
    bpy.context.collection.objects.link(cam)
    sc.camera = cam

    sun_d = bpy.data.lights.new("sun", "SUN")
    sun_d.energy = 2.2
    sun_d.angle = math.radians(1.5)          # 그림자 가장자리를 약간 부드럽게
    sun_d.color = (1.0, 0.94, 0.84)
    sun = bpy.data.objects.new("sun", sun_d)
    L = Vector(sun_vec())
    sun.rotation_euler = (-L).to_track_quat("-Z", "Y").to_euler()
    bpy.context.collection.objects.link(sun)


def to_normal_pass():
    """노멀맵 출력([15]§10.8 B3) — **머티리얼을 이미션으로 갈아** 노멀을 그대로 그린다.

    ⚠ 컴포지터 경로는 못 쓴다. Blender 5.x는 `scene.node_tree`가 사라졌고
      (`compositing_node_group`으로 대체), 새 컴포지터에는 `CompositorNodeMixRGB`·`Math`·
      `VecMath`가 아예 등록돼 있지 않다(실측). 인코딩(×0.5+0.5)을 할 노드가 없다.

    대신 각 머티리얼의 **BSDF Normal 입력에 물린 소켓**을 그대로 가져다 쓴다 —
    거기엔 이미 Bump와 Bevel이 합쳐져 있으므로 **모래 요철·모서리 마모까지 포함된** 노멀이 나온다.
    (Geometry 노드의 Normal을 쓰면 그 둘이 빠진다.)

    정사영 카메라라 카메라 공간 노멀이 곧 §3.1 접선공간이다(+x 오른쪽 · +z 화면 밖).
    §3.1은 아래가 +y인데 여기 y는 위가 +다 — **뷰어에서 g를 한 번 뒤집으면 맞는다**(B4).
    """
    for m in bpy.data.materials:
        nt = getattr(m, "node_tree", None)
        if not nt:
            continue
        out = next((n for n in nt.nodes if n.type == "OUTPUT_MATERIAL"), None)
        bsdf = nt.nodes.get("Principled BSDF")
        if not out:
            continue
        if bsdf is not None and bsdf.inputs["Normal"].is_linked:
            src = bsdf.inputs["Normal"].links[0].from_socket
        else:
            src = nt.nodes.new("ShaderNodeNewGeometry").outputs["Normal"]
        vt = nt.nodes.new("ShaderNodeVectorTransform")
        vt.vector_type, vt.convert_from, vt.convert_to = "VECTOR", "WORLD", "CAMERA"
        nt.links.new(src, vt.inputs[0])
        mul = nt.nodes.new("ShaderNodeVectorMath"); mul.operation = "MULTIPLY"
        # y·z 부호를 뒤집으면서 인코딩한다 — Blender 카메라 공간과 [15]§3.1이 서로 반대다.
        #   §3.1: +x 오른쪽 · **+y 아래** · **+z 화면 밖(관중 쪽)**
        #   Blender 카메라 공간: +y 위 · +z 화면 안쪽
        # 실측으로 확정한 값이다 — 바닥 평균이 (−0.003, +0.928, −0.349)로 나왔고
        # 기대한 (0, 0.940, +0.342)와 크기는 맞고 z 부호만 반대였다(두 부각 모두 동일).
        mul.inputs[1].default_value = (0.5, -0.5, -0.5)
        nt.links.new(vt.outputs[0], mul.inputs[0])
        addv = nt.nodes.new("ShaderNodeVectorMath"); addv.operation = "ADD"
        addv.inputs[1].default_value = (0.5, 0.5, 0.5)
        nt.links.new(mul.outputs[0], addv.inputs[0])
        em = nt.nodes.new("ShaderNodeEmission")
        nt.links.new(addv.outputs[0], em.inputs["Color"])
        nt.links.new(em.outputs[0], out.inputs["Surface"])

    sc = bpy.context.scene
    sc.view_settings.exposure = 0.0            # 노멀은 색이 아니라 데이터다 — 노출을 먹이면 안 된다
    # ⚠ Standard도 **sRGB 전달함수를 먹인다.** 첫 베이크에서 x=0이어야 할 바닥이 +0.47로 나왔다
    #   (선형 0.5 → sRGB 0.735 → 187). 노멀은 데이터이므로 반드시 Raw(선형 그대로)로 쓴다.
    sc.view_settings.view_transform = "Raw"
    sc.cycles.samples = 24                     # 이미션뿐이라 샘플이 많이 필요 없다
    bg = sc.world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.5, 0.5, 0.5, 1)   # 빈 곳 = 평면 노멀(0,0,1)의 인코딩값
    bg.inputs[1].default_value = 1.0


def _unused_setup_normal_pass(out_dir, name):
    """노멀맵 출력([15]§10.8 B3) — 카메라 공간 노멀 패스를 별도 파일로 뽑는다.

    A2·A3은 절차 텍스처에서 Sobel로 높이를 *추정*했다. 여기서는 지오메트리에서
    바로 나온다 — 코드는 줄고 결과는 좋아진다(§10.6).
    카메라가 정사영이라 **카메라 공간 노멀이 곧 §3.1의 접선공간**이다(+x 오른쪽 · +z 화면 밖).
    +y만 뒤집으면(§3.1은 아래가 +y) 뷰어 규약과 그대로 맞는다 — 그 반전은 B4에서 한 번에."""
    # ⚠ Blender 5.x는 `scene.node_tree`가 없다 — 컴포지터가 노드 그룹으로 바뀌었다.
    #   `scene.compositing_node_group`에 CompositorNodeTree를 새로 만들어 붙인다.
    #   그리고 `use_pass_normal`을 **먼저** 켜야 RLayers 노드에 Normal 출력이 생긴다(실측).
    sc = bpy.context.scene
    vl = bpy.context.view_layer
    vl.use_pass_normal = True
    ng = bpy.data.node_groups.new("arena_comp", "CompositorNodeTree")
    sc.compositing_node_group = ng
    nt = ng
    rl = nt.nodes.new("CompositorNodeRLayers")
    # 노멀은 −1~1이라 0~1로 옮겨 담는다(일반적인 파란 노멀맵 인코딩)
    mul = nt.nodes.new("CompositorNodeMixRGB"); mul.blend_type = "MULTIPLY"
    mul.inputs[0].default_value = 1.0; mul.inputs[2].default_value = (0.5, 0.5, 0.5, 1)
    addn = nt.nodes.new("CompositorNodeMixRGB"); addn.blend_type = "ADD"
    addn.inputs[0].default_value = 1.0; addn.inputs[2].default_value = (0.5, 0.5, 0.5, 1)
    fo = nt.nodes.new("CompositorNodeOutputFile")
    fo.base_path = out_dir
    fo.file_slots[0].path = name + "_"
    fo.format.file_format = "PNG"
    fo.format.color_depth = "16"          # 8비트면 노멀에 밴딩이 보인다
    nt.links.new(rl.outputs["Normal"], mul.inputs[1])
    nt.links.new(mul.outputs[0], addn.inputs[1])
    nt.links.new(addn.outputs[0], fo.inputs[0])


def render_dolly(a, out):
    """줌 경로 위의 N장을 굽는다([15]§10.17 · 1단계 정사영).

    프레임 f의 값은 **뷰어가 매 프레임 계산하는 것과 같은 식**이다:
        frac   = f / (N−1)
        CamTilt= TiltBasic + (TiltZoom − TiltBasic)·frac      (§2.4.3)
        camZoom= 1 + (ZoomFollow − 1)·frac
    정사영에서 '가까이 감'은 **ortho 폭을 줄이는 것**이다 → ORTHO_W / camZoom.
    부각은 tilt에서 역산한다(elevation = asin(CamTilt)) — §2.4.4 규칙 1과 같은 관계.

    ⚠ 이 벌은 **줌이 이미 구워져 있으므로** 뷰어가 다시 확대하면 안 된다.
      뷰어는 배경을 카메라 변환 **밖에서** 그리고 팬(pan)만 적용해야 한다(§10.17).
    """
    sc = bpy.context.scene
    cam = sc.camera
    base = os.path.splitext(out)[0]
    N = a.dolly
    # 오버스캔 — 가로와 세로는 **방식이 다르다.**
    #   가로: ortho_scale이 긴 변에 걸리므로 폭(resolution_x)만 키우면 세로 화각은 그대로다.
    #   세로: 화각을 넓히려면 resolution_y를 늘려야 한다(ortho_scale은 긴 변만 본다).
    if a.overscan > 1.0:
        sc.render.resolution_x = int(round(a.res * a.overscan))
    if a.overscan_y > 1.0:
        sc.render.resolution_y = int(round(a.res / ASPECT * a.overscan_y))
    for f in range(N):
        frac = f / max(1, N - 1)
        tilt = a.tilt_basic + (a.tilt_zoom - a.tilt_basic) * frac
        zoom = 1.0 + (a.zoom_follow - 1.0) * frac
        el = math.asin(max(-1.0, min(1.0, tilt)))
        cam.data.ortho_scale = ORTHO_W / zoom * a.overscan
        D = 80.0
        cam.location = (0.0, -D * math.cos(el), D * math.sin(el))
        cam.rotation_euler = (math.radians(90.0) - el, 0.0, 0.0)
        up = Vector((0.0, math.sin(el), math.cos(el)))
        # 프레이밍 이동도 줌에 비례한다 — 화면 픽셀 기준이 같아야 하므로 월드에서는 1/zoom
        cam.location = tuple(Vector(cam.location)
                             + up * ((a.cam_height - 0.5) * (940.0 / ASPECT) / PX_PER_M / zoom))
        sc.render.filepath = "%s_%02d.png" % (base, f)
        bpy.ops.render.render(write_still=True)
        print("WROTE %s_%02d.png  (frac %.3f · tilt %.4f · zoom %.3f)" % (base, f, frac, tilt, zoom))


def main():
    a = parse_args()
    clear()
    build(a)
    setup(a)
    out = a.out if os.path.isabs(a.out) else os.path.join(os.path.dirname(os.path.abspath(__file__)), a.out)
    if a.dolly > 0:
        render_dolly(a, out)
        return
    bpy.context.scene.render.filepath = out
    bpy.ops.render.render(write_still=True)
    print("WROTE", out)
    if a.normal:
        # 알베도를 먼저 굽고 나서 머티리얼을 갈아엎는다(되돌릴 필요가 없다 — 프로세스가 곧 끝난다)
        to_normal_pass()
        nout = os.path.splitext(out)[0] + "_n.png"
        bpy.context.scene.render.filepath = nout
        bpy.ops.render.render(write_still=True)
        print("WROTE", nout)


main()
