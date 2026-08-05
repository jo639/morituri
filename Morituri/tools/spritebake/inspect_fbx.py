"""
FBX 사전 점검 — 굽기 전에 "이 파일로 구울 수 있나"를 본다.

  blender --background --python inspect_fbx.py -- <fbx> [<fbx> ...]

보는 것 셋:
  MESH   : 메시가 없으면 Without Skin으로 받은 애니 전용 파일 → 캐릭터와 병합해야 굽는다.
  RANGE  : 액션 실제 구간 (씬 범위는 임포트 후에도 1~250 기본값이라 믿으면 안 된다).
  ROOT   : 루트(엉덩이) 본의 수평 이동량. 크면 In Place가 아니다 → 걸어나가서 프레임을 벗어난다.
"""
import sys, os
import bpy


def inspect(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=True)
    sc = bpy.context.scene

    meshes = [o for o in sc.objects if o.type == "MESH"]
    verts = sum(len(o.data.vertices) for o in meshes)

    lo = hi = None
    for o in sc.objects:
        act = o.animation_data.action if o.animation_data else None
        if not act:
            continue
        s, e = act.frame_range
        lo = s if lo is None else min(lo, s)
        hi = e if hi is None else max(hi, e)

    root_move = None
    arms = [o for o in sc.objects if o.type == "ARMATURE"]
    if arms and lo is not None:
        arm = arms[0]
        # 루트 후보: 부모 없는 본 중 첫째 (Mixamo는 Hips)
        roots = [b for b in arm.pose.bones if b.parent is None]
        if roots:
            rb = roots[0]
            pts = []
            for f in (lo, (lo + hi) / 2, hi):
                sc.frame_set(int(f), subframe=f - int(f))
                bpy.context.evaluated_depsgraph_get()
                w = arm.matrix_world @ rb.matrix.translation
                pts.append(w)
            dx = max(p.x for p in pts) - min(p.x for p in pts)
            dy = max(p.y for p in pts) - min(p.y for p in pts)
            root_move = (rb.name, max(dx, dy))

    name = os.path.basename(path)
    rng = f"{lo:.0f}~{hi:.0f}" if lo is not None else "없음"
    rm = f"{root_move[0]} {root_move[1]:.2f}" if root_move else "?"
    print(f"RESULT | {name} | MESH={len(meshes)}({verts}v) | RANGE={rng} | ROOT={rm}")


argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
for p in argv:
    try:
        inspect(p)
    except Exception as e:
        print(f"RESULT | {os.path.basename(p)} | ERROR: {e}")
