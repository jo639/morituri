"""
Tripo가 한 파일에 몰아준 무기 뭉치를 무기 ID별 GLB로 쪼갠다.

  blender --background --python split_props.py

출력 규약 (bake.py의 --prop이 기대하는 형태):
  - 긴 축을 **+Y**로 정렬
  - **손잡이 끝을 원점(0,0,0)**에 둠 → 손 본에 매달면 자루가 손에 잡힌다
  - 스케일은 그대로(bake.py가 --prop-len으로 정규화한다)

손잡이 판별은 '가는 쪽이 자루' 휴리스틱이다(망치·글라디우스·삼지창 모두 성립).
방패처럼 자루가 없는 물건은 무게중심을 원점에 둔다.
"""
import bpy, os, math
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "props", "medieval weapons 3d model (1).glb")
OUT = os.path.join(HERE, "props")

# 무기 ID → GLB 안의 조각 이름들 (렌더로 눈 확인함)
MAP = {
    "WPN_SWORD":      ["tripo_part_18"],   # 글라디우스
    "WPN_SPEAR":      ["tripo_part_2"],    # 삼지창
    "WPN_GREATSWORD": ["tripo_part_28"],   # 롬파이아
    "WPN_HAMMER":     ["tripo_part_8"],    # 전투 망치
    "WPN_SHIELD":     ["tripo_part_25"],   # 스쿠툼
    "WPN_WHIP":       ["tripo_part_22"],   # 감긴 채찍
    "WPN_DUALBLADES": ["tripo_part_20"],   # 곡검 1자루 (짝은 tripo_part_7 — 반대손용)
}
NO_GRIP = {"WPN_SHIELD", "WPN_WHIP"}   # 자루 축이 없는 물건 → 무게중심 정렬


def world_verts(o):
    m = o.matrix_world
    return [m @ v.co for v in o.data.vertices]


def align(o, no_grip):
    """긴 축을 +Y로 돌리고 손잡이 끝을 원점으로."""
    vs = world_verts(o)
    lo = Vector((min(v[i] for v in vs) for i in range(3)))
    hi = Vector((max(v[i] for v in vs) for i in range(3)))
    dims = hi - lo

    # XY 평면에서 긴 축 찾기 → +Y로 회전 (조각들이 평면에 누워 있다)
    if dims.x > dims.y:
        o.rotation_euler = (0, 0, math.radians(90))
        bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
        vs = world_verts(o)
        lo = Vector((min(v[i] for v in vs) for i in range(3)))
        hi = Vector((max(v[i] for v in vs) for i in range(3)))

    if not no_grip:
        # 양 끝 20% 구간의 XZ 퍼짐을 비교 — 가는 쪽이 자루
        y0, y1 = lo.y, hi.y
        seg = (y1 - y0) * 0.2
        def spread(a, b):
            sel = [v for v in vs if a <= v.y <= b]
            if not sel:
                return 1e9
            return (max(v.x for v in sel) - min(v.x for v in sel)) + \
                   (max(v.z for v in sel) - min(v.z for v in sel))
        if spread(y0, y0 + seg) > spread(y1 - seg, y1):
            # 굵은 쪽이 -Y에 있다 → 뒤집어서 자루를 -Y로
            o.rotation_euler = (0, 0, math.radians(180))
            bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
            vs = world_verts(o)
            lo = Vector((min(v[i] for v in vs) for i in range(3)))
            hi = Vector((max(v[i] for v in vs) for i in range(3)))
        anchor = Vector(((lo.x + hi.x) / 2, lo.y, (lo.z + hi.z) / 2))   # 자루 끝
    else:
        anchor = (lo + hi) / 2                                          # 무게중심

    o.location = -anchor
    bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)


def main():
    if not os.path.exists(SRC):
        raise SystemExit(f"[split] 원본 없음: {SRC}")
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=SRC)
    parts = {o.name: o for o in bpy.context.scene.objects if o.type == "MESH"}

    for wid, names in MAP.items():
        missing = [n for n in names if n not in parts]
        if missing:
            print(f"[split] SKIP {wid} — 조각 없음: {missing}")
            continue
        bpy.ops.object.select_all(action="DESELECT")
        for o in bpy.context.scene.objects:
            o.hide_set(False)
        # 대상만 복제해 정렬 (원본은 다음 무기를 위해 보존)
        copies = []
        for n in names:
            c = parts[n].copy()
            c.data = parts[n].data.copy()
            bpy.context.scene.collection.objects.link(c)
            copies.append(c)
        bpy.ops.object.select_all(action="DESELECT")
        for c in copies:
            c.select_set(True)
        bpy.context.view_layer.objects.active = copies[0]
        if len(copies) > 1:
            bpy.ops.object.join()
        obj = bpy.context.view_layer.objects.active
        obj.name = wid
        align(obj, wid in NO_GRIP)

        bpy.ops.object.select_all(action="DESELECT")
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
        path = os.path.join(OUT, f"{wid}.glb")
        # export_yup은 기본값(True)을 쓴다. Z-up으로 내보내면 임포터가 Y-up으로 해석해
        # 축이 한 번 더 돌아간다(길이축이 +Y가 아니라 Z로 들어온다). 왕복이 항등이 되게 둔다.
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True)
        d = obj.dimensions
        print(f"[split] {wid:<16} -> {os.path.basename(path)}  "
              f"dx={d.x:.3f} dy={d.y:.3f} dz={d.z:.3f}")
        bpy.data.objects.remove(obj, do_unlink=True)


if __name__ == "__main__":
    main()
