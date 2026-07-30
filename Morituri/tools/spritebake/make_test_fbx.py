"""
bake.py 연기시험(smoke test)용 FBX 생성 — Mixamo 파일이 없어도 파이프라인을 돌려볼 수 있게.

스킨드 메시 + 아마추어 + 본 애니 = Mixamo FBX와 같은 코드 경로를 타게 하는 게 목적이다.
(bake.py가 평가된 메시의 bbox를 제대로 잡는지는 실제 스킨 변형이 있어야 검증된다.)

  blender --background --python make_test_fbx.py -- --out source/_selftest.fbx
"""
import sys, os, math
import bpy


def out_path():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    p = argv[argv.index("--out") + 1] if "--out" in argv else "source/_selftest.fbx"
    if not os.path.isabs(p):
        p = os.path.join(os.path.dirname(os.path.abspath(__file__)), p)
    return os.path.normpath(p)


bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.frame_start, sc.frame_end = 1, 24

# 몸통: 사람 비율 비슷한 세로 박스 (발이 Z=0에 닿게)
bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, 0.9))
body = bpy.context.object
body.scale = (0.18, 0.12, 0.9)
bpy.ops.object.transform_apply(scale=True)

# 앞쪽 표식("코") — Mixamo 캐릭터가 보는 방향(-Y)에 둔다. 베이크 결과에서 화면 오른쪽에 나와야 정상.
bpy.ops.mesh.primitive_cube_add(size=1, location=(0, -0.22, 1.6))
nose = bpy.context.object
nose.scale = (0.06, 0.12, 0.06)
bpy.ops.object.transform_apply(scale=True)
bpy.ops.object.select_all(action="DESELECT")
nose.select_set(True)
body.select_set(True)
bpy.context.view_layer.objects.active = body
bpy.ops.object.join()

# 아마추어 2본: 허리 → 상체
bpy.ops.object.armature_add(location=(0, 0, 0))
arm = bpy.context.object
bpy.ops.object.mode_set(mode="EDIT")
eb = arm.data.edit_bones
root = eb[0]
root.head, root.tail = (0, 0, 0), (0, 0, 0.9)
upper = eb.new("upper")
upper.head, upper.tail = (0, 0, 0.9), (0, 0, 1.8)
upper.parent = root
bpy.ops.object.mode_set(mode="OBJECT")

# 스킨 바인딩 (자동 웨이트)
bpy.ops.object.select_all(action="DESELECT")
body.select_set(True)
arm.select_set(True)
bpy.context.view_layer.objects.active = arm
bpy.ops.object.parent_set(type="ARMATURE_AUTO")

# 상체를 앞뒤로 흔드는 24프레임 루프 — 스킨 변형이 프레임마다 bbox를 바꾼다
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="POSE")
pb = arm.pose.bones["upper"]
pb.rotation_mode = "XYZ"
for f, ang in ((1, 0), (7, 25), (13, 0), (19, -25), (24, 0)):
    pb.rotation_euler = (math.radians(ang), 0, 0)
    pb.keyframe_insert("rotation_euler", frame=f)
bpy.ops.object.mode_set(mode="OBJECT")

path = out_path()
os.makedirs(os.path.dirname(path), exist_ok=True)
bpy.ops.export_scene.fbx(filepath=path, use_selection=False, bake_anim=True,
                         add_leaf_bones=False)
print(f"[maketest] OK  {path}")
