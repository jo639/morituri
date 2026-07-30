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
from mathutils import Vector


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
    """메시 있는 리그 + 애니 액션을 한 씬에 조립. (meshes, armature) 반환."""
    bpy.ops.wm.read_factory_settings(use_empty=True)

    if char_path:
        meshes, arms = import_fbx(char_path)
        if not meshes:
            raise SystemExit(f"[bake] --char에 메시가 없다: {char_path}")
        if not arms:
            raise SystemExit(f"[bake] --char에 아마추어가 없다: {char_path}")
        char_arm = arms[0]

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
        return meshes, char_arm

    meshes, arms = import_fbx(anim_path)
    if not meshes:
        raise SystemExit(
            f"[bake] FBX에 메시가 없다: {anim_path}\n"
            "        Mixamo 애니 팩은 Without Skin이다 — --char로 캐릭터 FBX를 함께 지정할 것")
    return meshes, (arms[0] if arms else None)


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


def setup_render(res, view_size, center, azimuth, elevation, exposure, samples):
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
    for label, energy, zrot in (("key", 7.0, azimuth + 40), ("fill", 2.5, azimuth - 140)):
        ld = bpy.data.lights.new(label, type="SUN")
        ld.energy = energy
        o = bpy.data.objects.new(label, ld)
        sc.collection.objects.link(o)
        o.rotation_euler = (math.radians(55 if label == "key" else 70), 0, math.radians(zrot))

    world = bpy.data.worlds.new("w")
    if not world.use_nodes:
        world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (1, 1, 1, 1)      # 새 월드 기본색은 거의 검정이라 흰색으로
    bg.inputs[1].default_value = 0.8
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

    meshes, arm = build_scene(char, fbx)
    sc = bpy.context.scene

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

    stand_h = hi.z - lo.z
    center = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, (lo.z + hi.z) / 2))
    # 직립 높이가 목표 px가 되도록 화면 크기 역산 + 여유 1.8배(무기 스윙·팔 벌림)
    res = int(math.ceil(a.height * 1.8 / 2) * 2)
    view_size = stand_h * res / a.height
    cam = setup_render(res, view_size, center, a.azimuth, a.elevation, a.exposure, a.samples)
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
        cells.append(read_rgba(p))

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
        "refH": ch,
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
