# 아레나 최종 베이크([15]§10.8 B3) — 부각별 2벌 × (알베도 + 노멀)
# 해상도는 [15]§10.6: 940×560 논리 × BGS 2 = 1880×1120
$blender = "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($m in @(@{n="basic"; e=20.0}, @{n="zoom"; e=15.0})) {
    & $blender --background --python (Join-Path $here "blockout.py") -- `
        --out ("arena_" + $m.n + ".png") --elevation $m.e --res 1880 `
        --b2 --detail --normal --samples 256
}
