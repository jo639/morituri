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
ASPECT    = 940.0 / 440.0
ELEV      = 20.0                  # 기본 부각 (§2.4.1). 줌 벌은 15°
SUN_AZ, SUN_EL = 233.0, 41.0      # §3.3


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument("--out", default="blockout.png")
    p.add_argument("--res", type=int, default=940)
    p.add_argument("--elevation", type=float, default=ELEV)
    p.add_argument("--samples", type=int, default=48)
    p.add_argument("--exposure", type=float, default=-0.35, help="Standard 변환은 하이라이트가 바로 탄다")
    p.add_argument("--values", action="store_true",
                   help="§10.3 값 3단을 재질에 반영(기본: 전부 균일 회색 = 순수 블록아웃)")
    p.add_argument("--wall-h", type=float, default=3.5, help="포디움 벽 높이(m)")
    p.add_argument("--vela", type=float, default=1.0, help="차양 그림자 세기 0~1 (0=차양 제거)")
    p.add_argument("--no-velarium", action="store_true")
    p.add_argument("--no-fg", action="store_true", help="앞쪽 관중석 실루엣 제거")
    p.add_argument("--b2", action="store_true", help="B2 재질 팔레트(§10.5) 적용")
    p.add_argument("--detail", action="store_true", help="벽 석재 줄눈 + 모래 절차 요철(B2)")
    p.add_argument("--attic", action="store_true", help="아케이드/상단 관중석 — 이 카메라에선 프레임 밖(§10.11)")
    p.add_argument("--cut-keep", type=float, default=65.0, help="앞쪽 컷어웨이 각반경(도)")
    p.add_argument("--cut-fade", type=float, default=35.0, help="컷어웨이 페이드 폭(도)")
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


def sand_bump(m, rake=0.65, grain=0.35, dist=0.03):
    """모래 요철을 **절차 텍스처**로 준다. 지오메트리로 깎으면 폴리곤이 폭발하고,
    어차피 B3에서 노멀맵으로 구워질 것이라 여기서 셰이더로 넣는 게 맞다.
    rake = 동심 갈퀴 자국(Wave RINGS) · grain = 모래알(Noise)."""
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    wave = nt.nodes.new("ShaderNodeTexWave")
    wave.wave_type = "RINGS"
    wave.inputs["Scale"].default_value = 5.5
    wave.inputs["Distortion"].default_value = 2.5
    wave.inputs["Detail"].default_value = 2.0
    noise = nt.nodes.new("ShaderNodeTexNoise")
    noise.inputs["Scale"].default_value = 60.0
    noise.inputs["Detail"].default_value = 6.0
    mr = nt.nodes.new("ShaderNodeMath"); mr.operation = "MULTIPLY"; mr.inputs[1].default_value = rake
    mg = nt.nodes.new("ShaderNodeMath"); mg.operation = "MULTIPLY"; mg.inputs[1].default_value = grain
    add = nt.nodes.new("ShaderNodeMath"); add.operation = "ADD"
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 1.0
    bump.inputs["Distance"].default_value = dist * 2.2
    nt.links.new(wave.outputs["Fac"], mr.inputs[0])
    nt.links.new(noise.outputs["Fac"], mg.inputs[0])
    nt.links.new(mr.outputs[0], add.inputs[0])
    nt.links.new(mg.outputs[0], add.inputs[1])
    nt.links.new(add.outputs[0], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def wall_courses(name, r, z0, z1, mat_stone, mat_joint, courses=6, n=44, zscale=None, gaps=()):
    """포디움 벽 석재 — 돌(앞)/줄눈(뒤) 2겹. 앞 겹에 구멍을 내면 뒤의 어둠이 줄눈으로 보인다.
    단마다 반 칸씩 밀어 **엇갈림 쌓기**. 안쪽으로는 절대 튀어나오지 않는다 —
    r=12는 Sim 충돌 경계다(§1). 줄눈은 바깥(r+)으로만 판다."""
    lathe(name + "_joint", [(r + 0.035, z0), (r + 0.035, z1)], mat_joint, zscale=zscale, gaps=gaps)
    h = (z1 - z0) / courses
    for c in range(courses):
        za, zb = z0 + c * h, z0 + (c + 1) * h - h * 0.10      # 위쪽 10% = 가로 줄눈
        phase = (360.0 / n) * (0.5 if c % 2 else 0.0)          # 엇갈림
        lathe("%s_c%d" % (name, c), [(r, za), (r, zb)], mat_stone,
              zscale=zscale, gaps=list(gaps) + periodic_gaps(phase, phase + 360.0, n, duty=0.10))


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
        v_seat  = (0.24, 0.22, 0.20)     # 관중석 구조 — 프레임
        v_dark  = (0.045, 0.040, 0.038)  # 검은 공동
    else:
        v_floor = 0.62 if a.values else 0.5      # §10.3: 초점(밝게)
        v_wall  = 0.42 if a.values else 0.5      #         중간
        v_seat  = 0.20 if a.values else 0.5      #         프레임(어둡게)
        v_dark  = 0.03

    m_floor, m_wall, m_seat, m_dark = (mat("floor", v_floor, 0.95), mat("wall", v_wall, 0.80),
                                       mat("seat", v_seat), mat("dark", v_dark))
    m_joint = mat("joint", tuple(x * 0.62 for x in v_wall) if isinstance(v_wall, tuple)
                  else v_wall * 0.62)

    # 바닥 — Sim 원 그대로. 이 안에는 아무것도 없다(§1 계약)
    if a.detail:
        sand_bump(m_floor)
    disc("floor", ARENA_R, 0.0, m_floor)

    # 철문 2곳: 좌·우(az 0·180) ±7°
    GATES = [(353.0, 7.0), (173.0, 187.0)]

    # 포디움 벽 — 아레나를 '구덩이'로 만드는 요소(§10.4 #1).
    # 앞쪽은 컷어웨이: 낮은 턱(lip)만 남긴다 → 남단 검투사를 가리지 않는다(결정 3).
    WH = a.wall_h
    LIP = 0.35 / WH                      # 앞쪽에 남길 높이 비율(≈0.35 m 턱)
    cut = cutaway(keep=a.cut_keep, fade=a.cut_fade, floor=LIP)
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
    for i, (g0, g1) in enumerate(GATES):
        lathe("gate_void%d" % i,
              [(ARENA_R, 0.0), (ARENA_R, WH * 0.8), (ARENA_R + 3.0, WH * 0.8)],
              m_dark, gaps=[(g1, g0)], zscale=cut)

    # 관중석 3단 — 통로(vomitoria) 4곳으로 끊는다(§10.4 #3). 앞쪽은 같은 컷어웨이로 사라진다.
    VOM = [(43.0, 47.0), (133.0, 137.0), (223.0, 227.0), (313.0, 317.0)]
    cavea = [(ARENA_R + 0.6, WH), (ARENA_R + 2.4, WH),          # 순회 통로
             (ARENA_R + 2.4, WH + 1.2), (ARENA_R + 5.6, WH + 1.2),   # 1단
             (ARENA_R + 5.6, WH + 3.0), (ARENA_R + 9.2, WH + 3.0),   # 2단
             (ARENA_R + 9.2, WH + 5.2), (ARENA_R + 13.4, WH + 5.2),  # 3단
             (ARENA_R + 13.4, WH + 8.0), (ARENA_R + 17.0, WH + 8.0)]
    cut_seat = cutaway(keep=a.cut_keep, fade=a.cut_fade, floor=0.0)   # 앞쪽 관중석은 통째로 없앤다
    lathe("cavea", cavea, m_seat, gaps=VOM, zscale=cut_seat)

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
        fg = [(20.0, 0.0), (20.0, 2.60),                       # 뒤판 — 프레임 하단을 메운다
              (21.3, 2.95), (22.6, 3.40), (24.0, 3.90), (25.6, 4.45)]   # 단 4개
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
            top = 2.60 + (0.37 if tall else 0.16 + rnd() * 0.17)
            verts, faces = [], []
            for (r0, z0), (r1, z1) in [((20.6, 2.60), (20.6, top)), ((20.6, top), (21.2, top))]:
                c0, s0 = math.cos(math.radians(a0)), math.sin(math.radians(a0))
                c1, s1 = math.cos(math.radians(a0 + w)), math.sin(math.radians(a0 + w))
                b = len(verts)
                verts += [(r0 * c0, r0 * s0, z0), (r0 * c1, r0 * s1, z0),
                          (r1 * c1, r1 * s1, z1), (r1 * c0, r1 * s0, z1)]
                faces.append((b, b + 1, b + 2, b + 3))
            mesh_from("fg_tooth%d" % i, verts, faces, m_dark)

    if not a.no_velarium and a.vela > 0:
        # 차양 — 프레임 밖 상공. 보이지 않고 그림자만 남는다(라니스타 지시).
        H = 20.0
        Lx, Ly, Lz = sun_vec()
        off = Vector((-Lx / Lz * H, -Ly / Lz * H))   # 그림자가 아레나에 오도록 반대로 민다
        # 천은 빛을 완전히 막지 않는다. 알파를 주면 그림자가 '검은 막대'가 아니라 **눌린 띠**가 된다.
        m_vela = mat("velarium", 0.5)
        bsdf = m_vela.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Alpha"].default_value = 1.0 - 0.42 * a.vela
        verts, faces = [], []
        SLAT, GAP, N = 1.1, 2.3, 44
        for i in range(N):
            y0 = -40.0 + i * (SLAT + GAP)
            b = len(verts)
            verts += [(-34.0 + off.x, y0 + off.y, H), (34.0 + off.x, y0 + off.y, H),
                      (34.0 + off.x, y0 + SLAT + off.y, H), (-34.0 + off.x, y0 + SLAT + off.y, H)]
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


def main():
    a = parse_args()
    clear()
    build(a)
    setup(a)
    out = a.out if os.path.isabs(a.out) else os.path.join(os.path.dirname(os.path.abspath(__file__)), a.out)
    bpy.context.scene.render.filepath = out
    bpy.ops.render.render(write_still=True)
    print("WROTE", out)


main()
