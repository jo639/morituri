"""
스프라이트 베이크 — 3D(FBX) → 정사영 저해상 렌더 → 시트 PNG + sprites.json 조각.

Blender 안에서 돈다:
  blender --background --python bake.py -- --char "source/.../Paladin.fbx" \
                                            --fbx "source/.../sword and shield walk.fbx" \
                                            --anim walk_fwd

산출물(--out 폴더):
  <name>.png          가로 1줄 균등셀 시트
  <name>.anim.json    viewer.html의 animations 항목 하나 (merge_anim.py로 병합)

설계 메모:
- Mixamo 애니 팩은 보통 **Without Skin**이라 몸이 없다. --char로 캐릭터 FBX를 주면 그 리그에 애니
  액션을 얹어서 굽는다(뼈 이름이 mixamorig:*로 같아 그대로 붙는다).
- 팩 애니는 **In Place가 아니다**. 루트(Hips)의 수평 이동만큼 카메라를 같이 옮겨 제자리 렌더로 만든다.
  액션 F커브를 건드리지 않으므로 본 로컬축이 뭔지 알 필요가 없고, 수직 바운스는 그대로 살아 있다.
- 셀은 **균등 크기**다. 프레임별 트리밍을 안 하는 대신 전 프레임 알파 합집합 bbox로 한 번만 크롭한다
  → 셀 안에서 발 위치가 흔들리지 않는다(뷰어는 프레임 하단=지면, 가로 중앙 앵커).
- 고도각 기본 20°는 뷰어의 피트 투영(CamTilt=0.34 → asin≈19.9°)과 맞춘 값이다. 순수 측면은 --elevation 0.
- 방위각 기본 180°는 Mixamo 캐릭터(-Y를 봄)가 화면에서 **오른쪽을 보게** 하는 값이다(측정 확인).
"""
import sys, os, json, math

import bpy
import numpy as np
from mathutils import Vector, Matrix


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument("--fbx", required=True, help="애니 FBX")
    p.add_argument("--char", default=None, help="메시가 든 캐릭터 FBX (애니 파일이 Without Skin일 때 필수)")
    p.add_argument("--out", default=None, help="산출 폴더 (기본: sprites/bake)")
    p.add_argument("--name", default=None, help="파일 이름 (기본: --anim 값)")
    p.add_argument("--anim", default="walk_fwd", help="viewer의 animations 키")
    p.add_argument("--frames", type=int, default=8, help="시트에 담을 프레임 수")
    p.add_argument("--height", type=int, default=96, help="캐릭터 직립 목표 높이(px)")
    p.add_argument("--fps", type=int, default=10, help="viewer 재생 fps")
    p.add_argument("--azimuth", type=float, default=180.0)
    p.add_argument("--elevation", type=float, default=20.0)
    p.add_argument("--base-speed", type=float, default=2.0, help="viewer 폴백용 baseSpeed")
    p.add_argument("--exposure", type=float, default=1.0,
                   help="노출 보정 EV. 어두운 갑옷 기준값이라 밝은 캐릭터는 낮출 것")
    p.add_argument("--samples", type=int, default=96, help="Cycles 샘플 수")
    p.add_argument("--rim", type=float, default=0.0,
                   help="역광(림라이트) 세기. 어두운 배경에서 실루엣을 떼어낸다. 0=끔")
    p.add_argument("--contrast", type=float, default=1.0,
                   help="조명비. 키를 올리고 필·앰비를 내려 명암을 벌린다(후처리 대비와 달리 입체감이 남는다)")
    p.add_argument("--lines", type=float, default=0.0,
                   help="Freestyle 윤곽선 굵기(px). 0=끔. 해상도에 비례해 올려야 인상이 같다")
    p.add_argument("--toon", action="store_true",
                   help="Principled→Toon BSDF 교체. 계조가 끊겨 셀 셰이딩이 된다")
    p.add_argument("--supersample", type=int, default=1,
                   help="N배로 렌더 후 축소. 윤곽선 굵기 하한(1px)을 우회해 저해상에서 선을 얇게 앉힌다")
    # ── 무기 프롭 교체 (실루엣 시험 / 무기별 시트) ──
    p.add_argument("--prop", default=None, help="손에 쥘 무기 메시 (.obj/.fbx/.glb)")
    p.add_argument("--prop-bone", default="mixamorig:RightHand", help="프롭을 매달 본")
    p.add_argument("--prop-len", type=float, default=0.8,
                   help="프롭 최장축 길이(m). 캐릭터 키가 약 1.7m다")
    # 기본 -90은 측정값이다: 팔라딘의 원본 검을 손 본 좌표계로 변환하면 길이축이 **본의 +X**다
    # (bbox dx=101 vs dy=30, 본 길이 8.79). 프롭 길이축은 +Y로 정규화되므로 Z축 -90도로 X에 맞춘다.
    p.add_argument("--prop-rot", default="0,0,-90", help="프롭 회전 x,y,z(도) — 쥐는 각도 보정")
    p.add_argument("--grip", type=float, default=0.0,
                   help="자루의 어디를 쥐나 (0=끝, 0.4=중간쯤). 창처럼 중간을 쥐는 무기용")
    p.add_argument("--prop-off", default="0,0,0", help="프롭 위치 x,y,z(m) — 본 머리 기준")
    p.add_argument("--hide", default="", help="숨길 내장 메시 이름 조각, 쉼표 구분 (예: Sword,Shield)")
    p.add_argument("--keep-root-motion", action="store_true",
                   help="루트 모션 보정을 끈다(캐릭터가 화면을 가로질러 간다)")
    return p.parse_args(argv)


def import_fbx(path):
    """FBX를 불러오고 **이번에 추가된** 오브젝트만 돌려준다."""
    before = set(bpy.context.scene.objects)
    bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=True)
    added = [o for o in bpy.context.scene.objects if o not in before]
    return ([o for o in added if o.type == "MESH"],
            [o for o in added if o.type == "ARMATURE"])


def build_scene(char_path, anim_path):
    """메시 있는 리그 + 애니 액션을 한 씬에 조립. (meshes, armature, rest_h) 반환.

    rest_h = 애니를 얹기 **전** 바인드 자세의 세로 크기. 렌더 축척의 단일 기준이다.
    애니별 bbox로 축척을 잡으면 무릎이 굽은 걷기가 직립 높이까지 확대되어, 걷다가 공격할 때
    캐릭터가 15%씩 커졌다 작아진다(실측: refH가 포즈마다 93~107).
    """
    bpy.ops.wm.read_factory_settings(use_empty=True)

    if char_path:
        meshes, arms = import_fbx(char_path)
        if not meshes:
            raise SystemExit(f"[bake] --char에 메시가 없다: {char_path}")
        if not arms:
            raise SystemExit(f"[bake] --char에 아마추어가 없다: {char_path}")
        char_arm = arms[0]
        dg0 = bpy.context.evaluated_depsgraph_get()
        lo0, hi0 = world_bbox(meshes, dg0)
        rest_h = hi0.z - lo0.z

        _, anim_arms = import_fbx(anim_path)
        if not anim_arms:
            raise SystemExit(f"[bake] 애니 FBX에 아마추어가 없다: {anim_path}")
        src = anim_arms[0]
        act = src.animation_data.action if src.animation_data else None
        if not act:
            raise SystemExit(f"[bake] 애니 FBX에 액션이 없다: {anim_path}")

        # Mixamo 리그끼리는 본 이름(mixamorig:*)이 같아 액션이 그대로 붙는다.
        if not char_arm.animation_data:
            char_arm.animation_data_create()
        char_arm.animation_data.action = act

        for o in list(bpy.context.scene.objects):          # 애니 파일 쪽 오브젝트는 치운다
            if o == src or o.parent == src:
                bpy.data.objects.remove(o, do_unlink=True)
        return meshes, char_arm, rest_h

    meshes, arms = import_fbx(anim_path)
    if not meshes:
        raise SystemExit(
            f"[bake] FBX에 메시가 없다: {anim_path}\n"
            "        Mixamo 애니 팩은 Without Skin이다 — --char로 캐릭터 FBX를 함께 지정할 것")
    return meshes, (arms[0] if arms else None), None


def hide_meshes(meshes, names):
    """내장 메시를 이름으로 정확히 지운다. 남은 메시 목록 반환.

    **부분 문자열 매칭을 쓰지 않는다.** 이 팔라딘은 메시 이름이 내용과 뒤바뀌어 있어서
    (`..._Sword`가 몸통 7093v, `Paladin_J_Nordstrom`이 검 116v) 'Sword'로 지우면 몸이 사라진다.
    못 찾은 이름은 조용히 넘기지 않고 실패시킨다 — 무기가 안 지워진 채로 구워지는 게 더 나쁘다.
    """
    if not names:
        return meshes
    by_name = {o.name: o for o in meshes}
    missing = [n for n in names if n not in by_name]
    if missing:
        raise SystemExit(
            f"[bake] --hide 대상 없음: {', '.join(missing)}\n"
            f"        씬의 메시: {', '.join(by_name)}")
    keep = []
    for o in meshes:
        if o.name in names:
            bpy.data.objects.remove(o, do_unlink=True)
        else:
            keep.append(o)
    print(f"[bake] 숨김: {', '.join(names)}")
    return keep


def attach_prop(path, arm, bone_name, target_len, rot_deg, off_m, grip=0.0):
    """무기 메시를 불러와 손 본에 매단다. 반환: 프롭 오브젝트(월드 bbox 계산에 포함해야 한다).

    AI 3D 생성기마다 축·스케일 규약이 제각각이라, 최장축을 target_len으로 정규화한 뒤
    --prop-rot / --prop-off로 손에 맞춘다. 한 무기당 한 번만 맞추면 그 값이 계속 쓰인다.
    """
    ext = os.path.splitext(path)[1].lower()
    before = set(bpy.context.scene.objects)
    if ext == ".obj":
        bpy.ops.wm.obj_import(filepath=path)
    elif ext == ".fbx":
        bpy.ops.import_scene.fbx(filepath=path)
    elif ext in (".glb", ".gltf"):
        bpy.ops.import_scene.gltf(filepath=path)
    else:
        raise SystemExit(f"[bake] 지원 안 하는 프롭 형식: {ext} (.obj/.fbx/.glb)")
    added = [o for o in bpy.context.scene.objects if o not in before and o.type == "MESH"]
    if not added:
        raise SystemExit(f"[bake] 프롭에 메시가 없다: {path}")

    # 여러 조각이면 하나로 합친다
    bpy.ops.object.select_all(action="DESELECT")
    for o in added:
        o.select_set(True)
    bpy.context.view_layer.objects.active = added[0]
    if len(added) > 1:
        bpy.ops.object.join()
    prop = bpy.context.view_layer.objects.active
    prop.parent = None
    # glTF 임포터는 rotation_mode를 QUATERNION으로 둔다 → rotation_euler 대입이 **조용히 무시된다**.
    # 회전이 안 먹는 게 아니라 값이 반영조차 안 됐다(실측: matrix_basis 오일러가 계속 0).
    prop.rotation_mode = "XYZ"
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)

    # ── 축 규약을 여기서 강제한다: 길이축 → +Y, 자루 끝 → 원점 ──
    # 원본 파일이 어느 축으로 오든(GLB 왕복만 해도 축이 바뀐다) 여기서 맞춰야
    # --prop-rot이 예측 가능하게 동작한다. 실제로 길이축이 X로 들어와 X축 회전이
    # 무기를 제자리에서 돌리기만 했다.
    d = prop.dimensions
    axis = max(("x", d.x), ("y", d.y), ("z", d.z), key=lambda t: t[1])[0]
    if axis == "x":
        prop.rotation_euler = (0, 0, math.radians(90))      # X → Y
    elif axis == "z":
        prop.rotation_euler = (math.radians(-90), 0, 0)     # Z → Y
    if axis != "y":
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)

    # 최장축을 target_len으로 정규화
    dims = prop.dimensions
    longest = max(dims.x, dims.y, dims.z)
    if longest <= 0:
        raise SystemExit("[bake] 프롭 크기가 0이다")
    s = target_len / longest
    prop.scale = (s, s, s)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

    # 자루 끝(가는 쪽)을 원점으로. 양 끝 20% 구간의 XZ 퍼짐을 비교한다.
    vs = [v.co for v in prop.data.vertices]
    y0 = min(v.y for v in vs); y1 = max(v.y for v in vs)
    seg = (y1 - y0) * 0.2

    def spread(a, b):
        sel = [v for v in vs if a <= v.y <= b]
        if not sel:
            return 1e9
        return (max(v.x for v in sel) - min(v.x for v in sel)) + \
               (max(v.z for v in sel) - min(v.z for v in sel))

    if spread(y0, y0 + seg) > spread(y1 - seg, y1):
        prop.rotation_euler = (0, 0, math.radians(180))     # 굵은 쪽이 -Y면 뒤집는다
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
        vs = [v.co for v in prop.data.vertices]
        y0 = min(v.y for v in vs)
    xs = [v.x for v in vs]; zs = [v.z for v in vs]
    y1 = max(v.y for v in vs)
    # grip: 자루 끝(0)에서 길이의 몇 %를 손이 잡는가. 창은 중간을 쥔다.
    prop.location = (-(min(xs) + max(xs)) / 2,
                     -(y0 + (y1 - y0) * grip),
                     -(min(zs) + max(zs)) / 2)
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)

    # BONE 부모의 로컬 공간은 **본 꼬리**가 원점이고 +Y가 본 방향이다.
    # parent_inverse를 항등으로 두고 -Y로 본 길이만큼 돌아가면 본 머리(=손목)에 앉는다.
    # (머리 기준 역행렬을 끼우는 방식은 꼬리 오프셋이 사이에 끼어 상쇄되지 않는다 — 프롭이
    #  월드 원점에 남는다. 실측으로 확인.)
    bone = arm.data.bones.get(bone_name)
    if not bone:
        raise SystemExit(f"[bake] 본 없음: {bone_name}")
    prop.parent = arm
    prop.parent_type = "BONE"
    prop.parent_bone = bone_name
    prop.matrix_parent_inverse = Matrix.Identity(4)
    # Mixamo 아마추어는 0.01 스케일을 물고 있다(본 길이 8.79 vs 캐릭터 1.72m). 부모를 붙이면
    # 자식이 그걸 상속받아 1.4m 무기가 1.4cm가 된다 — 역수로 상쇄한다.
    asc = arm.matrix_world.to_scale()
    prop.scale = (1 / asc.x, 1 / asc.y, 1 / asc.z)
    # 위치는 본 공간(아마추어 로컬 단위)이라 bone.length를 그대로 쓴다. 오프셋은 미터라 환산.
    prop.location = Vector((off_m[0] / asc.x,
                            -bone.length + off_m[1] / asc.y,
                            off_m[2] / asc.z))
    prop.rotation_euler = tuple(math.radians(v) for v in rot_deg)
    return prop


def setup_lines(thickness):
    """Freestyle 윤곽선 — 실루엣·경계·크리스만. 3D 위에 2D 선을 얹는 '아케인'류 인상의 핵심."""
    sc = bpy.context.scene
    sc.render.use_freestyle = True
    # ABSOLUTE 모드에서 굵기는 linestyle.thickness 하나로만 준다.
    # render.line_thickness까지 같이 주면 곱해져서 선이 형체를 덮는다(실측).
    sc.render.line_thickness_mode = "ABSOLUTE"
    vl = sc.view_layers[0]
    vl.use_freestyle = True
    fs = vl.freestyle_settings
    for old in list(fs.linesets):
        fs.linesets.remove(old)
    lineset = fs.linesets.new("outline")
    lineset.select_silhouette = True
    lineset.select_border = True
    # 크리스는 끈다. 8000버텍스 캐릭터에선 내부 모서리마다 선이 그어져 저해상에선 형체가
    # 선으로 덮여 갈색 진흙이 된다(실측). 내부 선이 필요하면 crease_angle을 크게 잡고 따로 켤 것.
    lineset.select_crease = False
    st = lineset.linestyle
    # 순수 검정. 어두운 값이라도 선형이면 노출·sRGB 변환에 들려 갈색으로 뜬다(실측).
    st.color = (0.0, 0.0, 0.0)
    st.thickness = thickness


def make_toon():
    """Principled를 Toon BSDF로 갈아끼운다. 베이스 컬러 텍스처는 그대로 물려 색은 유지."""
    for m in bpy.data.materials:
        if not m.use_nodes or not m.node_tree:
            continue
        nt = m.node_tree
        bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
        out = next((n for n in nt.nodes if n.type == "OUTPUT_MATERIAL"), None)
        if not bsdf or not out:
            continue
        toon = nt.nodes.new("ShaderNodeBsdfToon")
        try:
            toon.component = "DIFFUSE"
            toon.inputs["Size"].default_value = 0.5
            toon.inputs["Smooth"].default_value = 0.05
        except (KeyError, TypeError):
            pass
        src = bsdf.inputs["Base Color"]
        if src.is_linked:
            nt.links.new(src.links[0].from_socket, toon.inputs["Color"])
        else:
            toon.inputs["Color"].default_value = src.default_value
        nt.links.new(toon.outputs["BSDF"], out.inputs["Surface"])


def root_bone(arm):
    """Mixamo 루트(Hips) = 부모 없는 첫 본."""
    if not arm:
        return None
    roots = [b for b in arm.pose.bones if b.parent is None]
    return roots[0] if roots else None


def action_range(arm):
    """액션의 실제 구간.

    FBX 임포트는 씬 프레임 범위를 기본값(1~250) 그대로 두는 경우가 있다. 씬 범위로 샘플링하면
    액션이 끝난 뒤의 정지 포즈만 반복해서 찍힌다 — 걷기 애니가 정지 8장으로 나온다.
    """
    act = arm.animation_data.action if arm and arm.animation_data else None
    if not act:
        print("[bake] 경고: 액션이 없다 — 씬 범위로 폴백(정지 프레임만 나올 수 있음)")
        return bpy.context.scene.frame_start, bpy.context.scene.frame_end
    s, e = act.frame_range
    return s, e


def sample_frames(start, end, n):
    """액션 구간을 균등 샘플. 마지막 프레임은 첫 프레임과 같은 루프 중복이라 제외."""
    span = max(1.0, end - start)
    return [start + span * i / n for i in range(n)]


def world_bbox(meshes, depsgraph):
    """평가된(=애니 적용된) 메시들의 월드 bbox."""
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for o in meshes:
        ev = o.evaluated_get(depsgraph)
        mw = ev.matrix_world
        for c in ev.bound_box:
            w = mw @ Vector(c)
            lo = Vector((min(lo[i], w[i]) for i in range(3)))
            hi = Vector((max(hi[i], w[i]) for i in range(3)))
    return lo, hi


def setup_render(res, view_size, center, azimuth, elevation, exposure, samples, rim, contrast):
    sc = bpy.context.scene

    # Cycles CPU 고정. EEVEE는 headless에서 GPU 컨텍스트를 타는데 이 환경의 Intel 드라이버가
    # 종료 시 죽었고, Blender 5.2엔 BLENDER_EEVEE_NEXT 이름도 없다. 174px 8장이라 CPU로 충분하다.
    sc.render.engine = "CYCLES"
    sc.cycles.device = "CPU"
    sc.cycles.samples = samples

    sc.render.resolution_x = res
    sc.render.resolution_y = res
    sc.render.resolution_percentage = 100
    sc.render.film_transparent = True
    sc.render.filter_size = 0.0            # 안티에일리어싱 끔 — 픽셀 경계가 뭉개지면 픽셀아트가 아니다
    sc.render.image_settings.file_format = "PNG"
    sc.render.image_settings.color_mode = "RGBA"
    # AgX/Filmic은 색을 눕혀버린다. 픽셀아트는 Standard로 받아야 팔레트가 산다.
    try:
        sc.view_settings.view_transform = "Standard"
    except TypeError:
        pass
    sc.view_settings.exposure = exposure

    cam_data = bpy.data.cameras.new("bakecam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = view_size
    cam = bpy.data.objects.new("bakecam", cam_data)
    sc.collection.objects.link(cam)
    sc.camera = cam

    az, el = math.radians(azimuth), math.radians(elevation)
    d = Vector((math.cos(el) * math.cos(az), math.cos(el) * math.sin(az), math.sin(el)))
    cam.location = center + d * (view_size * 4)
    cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()

    # 조명 리그: 확산 반사는 알베도×조도/π라, 갑옷처럼 알베도가 낮은 재질은 태양 하나로는 새까맣게 나온다.
    # 키(카메라 쪽) + 필(반대편) + 흰 앰비언트 3단으로 실루엣 안쪽 형태가 읽히게 만든다.
    # 림(역광)은 카메라 반대편에서 쏴 실루엣 가장자리만 밝힌다 — 어두운 아레나에서 형체를 떼어낸다.
    # 평면 먹선보다 나은 이유: 배경이 어두울 때 어두운 외곽선은 오히려 배경에 먹힌다.
    # 대비를 올릴 때 필·앰비를 c로 나누면 그늘에 **바닥이 없어진다** — 팔뚝·손처럼 몸에 붙어
    # 키를 못 받는 부위가 통째로 검게 잠겨 잘려 보인다(실측: 대비 2.0에서 양손이 사라졌다).
    # sqrt로 완만하게 내려 명암비는 벌리되 그림자 바닥을 남긴다.
    c = max(0.1, contrast)
    cs = math.sqrt(c)
    rig = [("key", 7.0 * cs, azimuth + 40, 55), ("fill", 2.5 / cs, azimuth - 140, 70)]
    if rim > 0:
        rig.append(("rim", rim, azimuth + 180, 35))
    for label, energy, zrot, xrot in rig:
        ld = bpy.data.lights.new(label, type="SUN")
        ld.energy = energy
        o = bpy.data.objects.new(label, ld)
        sc.collection.objects.link(o)
        o.rotation_euler = (math.radians(xrot), 0, math.radians(zrot))

    world = bpy.data.worlds.new("w")
    if not world.use_nodes:
        world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (1, 1, 1, 1)      # 새 월드 기본색은 거의 검정이라 흰색으로
    bg.inputs[1].default_value = 0.8 / cs   # 앰비언트 = 그림자 바닥. c로 나누면 손이 사라진다
    sc.world = world
    return cam


def read_rgba(path):
    """PNG → (h,w,4) uint8, 위가 0행. Blender 픽셀은 하단부터라 뒤집는다."""
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"   # 원본 바이트 그대로 (sRGB 재변환 방지)
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    arr = px.reshape(h, w, 4)[::-1]
    bpy.data.images.remove(img)
    return np.clip(arr * 255.0 + 0.5, 0, 255).astype(np.uint8)


def write_rgba(path, arr):
    h, w, _ = arr.shape
    img = bpy.data.images.new("sheet", width=w, height=h, alpha=True)
    img.colorspace_settings.name = "Non-Color"
    img.pixels.foreach_set((arr[::-1].astype(np.float32) / 255.0).reshape(-1))
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    bpy.data.images.remove(img)


def downsample(arr, k):
    """k배 박스 축소. 알파 프리멀티플라이 후 평균 — 안 하면 투명 픽셀의 검정이 섞여 가장자리가 어두워진다."""
    if k <= 1:
        return arr
    h, w, _ = arr.shape
    h2, w2 = h // k, w // k
    a = arr[:h2 * k, :w2 * k].astype(np.float32)
    al = a[:, :, 3:4] / 255.0
    pre = np.concatenate([a[:, :, :3] * al, al], axis=2)
    avg = pre.reshape(h2, k, w2, k, 4).mean(axis=(1, 3))
    out_al = avg[:, :, 3:4]
    rgb = np.divide(avg[:, :, :3], np.maximum(out_al, 1e-6))
    out = np.concatenate([rgb, out_al * 255.0], axis=2)
    return np.clip(out + 0.5, 0, 255).astype(np.uint8)


def alpha_bbox(arr):
    ys, xs = np.nonzero(arr[:, :, 3])
    if len(xs) == 0:
        return None
    return xs.min(), ys.min(), xs.max() + 1, ys.max() + 1


def main():
    a = parse_args()
    fbx = os.path.abspath(a.fbx)
    if not os.path.exists(fbx):
        raise SystemExit(f"[bake] FBX 없음: {fbx}")
    char = os.path.abspath(a.char) if a.char else None
    if char and not os.path.exists(char):
        raise SystemExit(f"[bake] 캐릭터 FBX 없음: {char}")

    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.normpath(a.out if a.out else os.path.join(
        here, "..", "..", "Morituri.Headless", "sprites", "bake"))
    os.makedirs(out_dir, exist_ok=True)
    name = a.name or a.anim

    meshes, arm, rest_h = build_scene(char, fbx)
    sc = bpy.context.scene

    # 무기 교체: 내장 검·방패를 빼고 새 프롭을 손에 매단다.
    # rest_h(축척 기준)는 프롭 부착 **전** 몸 높이라 무기를 바꿔도 캐릭터 크기가 흔들리지 않는다.
    meshes = hide_meshes(meshes, [s.strip() for s in a.hide.split(",") if s.strip()])
    if a.prop:
        prop_path = os.path.abspath(a.prop)
        if not os.path.exists(prop_path):
            raise SystemExit(f"[bake] 프롭 없음: {prop_path}")
        rot = [float(v) for v in a.prop_rot.split(",")]
        off = [float(v) for v in a.prop_off.split(",")]
        meshes.append(attach_prop(prop_path, arm, a.prop_bone, a.prop_len, rot, off, a.grip))
        print(f"[bake] 프롭 {os.path.basename(prop_path)} → {a.prop_bone} "
              f"(길이 {a.prop_len}m · 회전 {a.prop_rot} · 오프셋 {a.prop_off})")

    f0, f1 = action_range(arm)
    times = sample_frames(f0, f1, a.frames)
    rb = None if a.keep_root_motion else root_bone(arm)
    print(f"[bake] 액션 {f0:.0f}~{f1:.0f} → {a.frames}프레임 · "
          f"루트보정 {'끔' if not rb else rb.name}")

    # 1패스: 루트 수평이동을 뺀 좌표계에서 전 프레임 합집합 bbox를 잡는다(팔다리·무기가 잘리지 않게).
    lo = Vector((1e9, 1e9, 1e9)); hi = Vector((-1e9, -1e9, -1e9))
    offsets = []
    root0 = None
    for t in times:
        sc.frame_set(int(t), subframe=t - int(t))
        dg = bpy.context.evaluated_depsgraph_get()   # 프레임마다 새로 — 캐시된 dg는 포즈가 안 따라온다
        if rb:
            rw = arm.matrix_world @ rb.matrix.translation
            if root0 is None:
                root0 = rw.copy()
            off = Vector((rw.x - root0.x, rw.y - root0.y, 0))
        else:
            off = Vector((0, 0, 0))
        offsets.append(off)
        l, h = world_bbox(meshes, dg)
        l, h = l - off, h - off
        lo = Vector((min(lo[i], l[i]) for i in range(3)))
        hi = Vector((max(hi[i], h[i]) for i in range(3)))

    # 축척 기준은 **바인드 자세 높이 하나로 고정**한다. 애니별 bbox로 잡으면 포즈마다 배율이
    # 달라져 상태 전환 때 캐릭터가 커졌다 작아진다. 프레이밍(center)만 애니 bbox를 쓴다.
    stand_h = rest_h if rest_h else (hi.z - lo.z)
    center = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2))
    # 직립 높이가 목표 px가 되도록 화면 크기 역산 + 여유 2.2배(치켜든 무기·팔 벌림이 잘리지 않게)
    res = int(math.ceil(a.height * 2.2 / 2) * 2)
    view_size = stand_h * res / a.height
    # 수퍼샘플: 화각(view_size)은 최종 해상도 기준 그대로 두고 픽셀 수만 늘린다 → 프레이밍 불변.
    ss = max(1, a.supersample)
    cam = setup_render(res * ss, view_size, center, a.azimuth, a.elevation,
                       a.exposure, a.samples, a.rim, a.contrast)
    if a.lines > 0:
        setup_lines(a.lines * ss)   # 굵기는 최종 공간 기준으로 받아 렌더 공간으로 환산
    if a.toon:
        make_toon()
    cam_home = cam.location.copy()

    # 2패스: 카메라를 루트 이동만큼 같이 옮겨 제자리 렌더(정사영이라 평행이동=화면 이동과 1:1).
    tmp = os.path.join(out_dir, "_tmp")
    os.makedirs(tmp, exist_ok=True)
    cells = []
    for i, (t, off) in enumerate(zip(times, offsets)):
        sc.frame_set(int(t), subframe=t - int(t))
        cam.location = cam_home + off
        p = os.path.join(tmp, f"{name}_{i:02d}.png")
        sc.render.filepath = p
        bpy.ops.render.render(write_still=True)
        cells.append(downsample(read_rgba(p), ss))

    boxes = [b for b in (alpha_bbox(c) for c in cells) if b]
    if not boxes:
        raise SystemExit("[bake] 렌더가 전부 비었다 — 카메라 방위각/고도각 또는 FBX 스케일 확인")
    x0 = min(b[0] for b in boxes); y0 = min(b[1] for b in boxes)
    x1 = max(b[2] for b in boxes); y1 = max(b[3] for b in boxes)
    cells = [c[y0:y1, x0:x1] for c in cells]

    ch, cw, _ = cells[0].shape
    png = os.path.join(out_dir, f"{name}.png")
    write_rgba(png, np.concatenate(cells, axis=1))

    anim = {
        "image": f"bake/{name}.png",
        "fps": a.fps,
        # 직립 환산 키 = 목표 높이 그 자체. 셀 높이(ch)를 쓰면 애니마다 값이 달라져
        # 뷰어 배율 k=(FIGH·sc)/refH가 흔들린다 — 포즈 전환 때 캐릭터가 커졌다 작아진다.
        "refH": a.height,
        "baseSpeed": a.base_speed,
        "frames": [{"x": i * cw, "y": 0, "w": cw, "h": ch} for i in range(len(cells))],
    }
    with open(os.path.join(out_dir, f"{name}.anim.json"), "w", encoding="utf-8") as f:
        json.dump({a.anim: anim}, f, ensure_ascii=False, indent=2)

    for f_ in os.listdir(tmp):
        os.remove(os.path.join(tmp, f_))
    os.rmdir(tmp)

    print(f"[bake] OK  {png}  셀 {cw}x{ch} × {len(cells)}프레임")


if __name__ == "__main__":
    main()
