# 9포즈 배치 — 확정 룩(96px · lines 1.0 · supersample 1 · rim 14 · contrast 2.0 · elevation 20).
# 값의 근거와 묶임은 [15]§2.4 / sprites/README.md 참고. 임의로 바꾸면 뷰어 투영과 어긋난다.
#
#   powershell -File bake_all.ps1
#
# walk_bwd · dash_bwd는 굽지 않는다 — walk_fwd 프레임을 역순으로 참조해 만든다(merge_poses.py).
# 팩에 뒷걸음 애니가 없어서 내린 절충이다. 전용 애니가 생기면 여기에 추가할 것.

$blender = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pack    = Join-Path $here "source\Sword and Shield Pack"
$charFbx = Join-Path $pack "Paladin WProp J Nordstrom.fbx"

# anim키 = FBX파일, 프레임수
$poses = @(
    @{ key = "idle";         fbx = "sword and shield idle.fbx";       frames = 24 },
    @{ key = "walk_fwd";     fbx = "sword and shield walk.fbx";       frames = 24 },
    @{ key = "guard";        fbx = "sword and shield block idle.fbx"; frames = 16 },
    @{ key = "light_attack"; fbx = "sword and shield slash.fbx";      frames = 16 },
    @{ key = "heavy_attack"; fbx = "sword and shield attack.fbx";     frames = 20 },
    @{ key = "hurt_light";   fbx = "sword and shield impact.fbx";     frames = 10 },
    @{ key = "hurt_heavy";   fbx = "sword and shield impact (2).fbx"; frames = 12 },
    @{ key = "down";         fbx = "sword and shield death.fbx";      frames = 20 },
    @{ key = "taunt";        fbx = "sword and shield power up.fbx";   frames = 20 }
)

$sw = [Diagnostics.Stopwatch]::StartNew()
foreach ($p in $poses) {
    $src = Join-Path $pack $p.fbx
    if (-not (Test-Path $src)) { Write-Host "SKIP (없음): $($p.fbx)"; continue }
    $t = [Diagnostics.Stopwatch]::StartNew()
    & $blender --background --python (Join-Path $here "bake.py") -- `
        --char $charFbx --fbx $src --anim $p.key --name $p.key `
        --frames $p.frames --height 96 --rim 14 --contrast 2.0 --lines 1.0 `
        --supersample 1 --elevation 20 | Select-String "bake\] OK"
    Write-Host ("  -> {0} ({1}프레임) {2}초" -f $p.key, $p.frames, [math]::Round($t.Elapsed.TotalSeconds, 1))
}
Write-Host ("전체 {0}포즈 / {1}분" -f $poses.Count, [math]::Round($sw.Elapsed.TotalMinutes, 1))
