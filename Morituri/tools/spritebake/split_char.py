"""
무기가 붙은 캐릭터 GLB를 **몸 / 무기**로 분리한다.

  blender --background --python split_char.py -- --src "...glb" --name ROOSTER --out props

왜 분리하나:
  Mixamo 오토리거는 근접한 본에 가중치를 준다. 무기가 다리 옆에 늘어져 있으면 다리에 스킨되어
  걸을 때 무기가 휘어진다. 몸만 올려서 리깅한 뒤, 무기는 **원래 좌표 그대로** 손 본에 다시 붙인다.

핵심: 두 파일 모두 **월드 좌표를 보존**한다. 원본에서 무기는 이미 손에 정확히 쥐어져 있고
그 자세가 곧 바인드 포즈이므로, 리깅 후 같은 좌표에 놓기만 하면 파지가 정확하다.
(파지 위치를 계산으로 알아내려는 시도는 실패했다 — 원본이 답을 갖고 있으니 계산하지 않는다.)
"""
import bpy, sys, os, math
from mathutils import Vector


def args():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument("--src", required=True)
    p.add_argument("--name", required=True, help="출력 이름 접두 (예: ROOSTER)")
    p.add_argument("--out", default=None)
    p.add_argument("--preview", default=None, help="조각별 렌더를 저장할 폴더 (식별용)")
    p.add_argument("--weapon-part", type=int, default=None,
                   help="무기인 조각 번호(버텍스 많은 순 0부터). 먼저 --preview로 눈 확인할 것")
    return p.parse_args(argv)


def parts_report(objs):
    dg = bpy.context.evaluated_depsgraph_get()
    rows = []
    for o in objs:
        ev = o.evaluated_get(dg)
        pts = [ev.matrix_world @ Vector(c) for c in ev.bound_box]
        lo = Vector((min(p[i] for p in pts) for i in range(3)))
        hi = Vector((max(p[i] for p in pts) for i in range(3)))
        rows.append((o, lo, hi, hi - lo))
    return rows


def main():
    a = args()
    out_dir = os.path.abspath(a.out) if a.out else os.path.dirname(os.path.abspath(a.src))
    os.makedirs(out_dir, exist_ok=True)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.abspath(a.src))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        raise SystemExit("[split] 메시가 없다")

    # 하나로 합친 뒤 느슨한 조각으로 분리 (무기가 몸과 용접돼 있지 않다면 이걸로 갈린다)
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.rotation_mode = "XYZ"
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # AI 생성 메시는 용접이 안 돼 있다(실측: 31,301버텍스가 6,094조각으로 흩어짐).
    # 그대로면 연결성 분리가 무의미하고 Mixamo 오토리깅도 위험하다 → 먼저 거리 병합.
    before_v = len(obj.data.vertices)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=0.0002)
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.object.mode_set(mode="OBJECT")
    print("WELD | 버텍스 %d → %d" % (before_v, len(obj.data.vertices)))

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")

    parts = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    rows = parts_report(parts)
    rows.sort(key=lambda r: -len(r[0].data.vertices))
    for o, lo, hi, d in rows:
        print("PART | %-30s v=%6d | 크기 %.3f x %.3f x %.3f | z %.3f~%.3f"
              % (o.name[:30], len(o.data.vertices), d.x, d.y, d.z, lo.z, hi.z))
    print("PARTS | 총 %d조각" % len(parts))

    if a.preview:
        render_parts([r[0] for r in rows], rows, a.preview)

    if a.weapon_part is not None:
        wobj = rows[a.weapon_part][0]
        body = [r[0] for i, r in enumerate(rows) if i != a.weapon_part]
        export(body, os.path.join(out_dir, f"{a.name}_body.glb"))
        export([wobj], os.path.join(out_dir, f"{a.name}_weapon.glb"))
        print(f"[split] 몸 → {a.name}_body.glb ({sum(len(o.data.vertices) for o in body)}v)")
        print(f"[split] 무기 → {a.name}_weapon.glb ({len(wobj.data.vertices)}v) · 월드좌표 보존")
    return parts, rows, out_dir, a


def export(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True)


def render_parts(objs, rows, out):
    """조각별 렌더 — 어느 게 무기인지 눈으로 확인한다(bbox 추론 금지)."""
    os.makedirs(out, exist_ok=True)
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"; sc.cycles.device = "CPU"; sc.cycles.samples = 16
    sc.render.resolution_x = sc.render.resolution_y = 300
    sc.render.film_transparent = True
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGBA"
    sc.view_settings.view_transform = "Standard"; sc.view_settings.exposure = 1.0
    w = bpy.data.worlds.new("w"); w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (1, 1, 1, 1)
    w.node_tree.nodes["Background"].inputs[1].default_value = 1.1
    sc.world = w
    ld = bpy.data.lights.new("k", type="SUN"); ld.energy = 4.0
    lo_ = bpy.data.objects.new("k", ld); sc.collection.objects.link(lo_)
    lo_.rotation_euler = (math.radians(50), 0, math.radians(210))
    cd = bpy.data.cameras.new("c"); cd.type = "ORTHO"
    cam = bpy.data.objects.new("c", cd); sc.collection.objects.link(cam); sc.camera = cam
    allpts = []
    for _, lo, hi, _d in rows:
        allpts += [lo, hi]
    L = Vector((min(p[i] for p in allpts) for i in range(3)))
    H = Vector((max(p[i] for p in allpts) for i in range(3)))
    ctr = (L + H) / 2
    cd.ortho_scale = max(H - L) * 1.1
    cam.location = (ctr.x - 5, ctr.y, ctr.z)
    cam.rotation_euler = (math.radians(90), 0, math.radians(-90))   # 측면
    for i, o in enumerate(objs):
        for x in objs:
            x.hide_render = (x is not o)
        sc.render.filepath = os.path.join(out, "part%d.png" % i)
        bpy.ops.render.render(write_still=True)
        print("RENDER | part%d = %s" % (i, o.name[:24]))
    for x in objs:
        x.hide_render = False


if __name__ == "__main__":
    main()
