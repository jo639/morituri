using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 무기 데미지 자동 정규화 (M3-A2). 무기 스탯은 강결합 비선형계라 손튜닝이 whack-a-mole이 된다
/// (쌍검 누르면 검이 솟음). 좌표하강으로 무기별 데미지 배율을 조정해 빌드 매트릭스 파워를 50 수렴.
/// 데미지는 승률에 단조·지배적이라 1차 정규화기로 적합 (템포 등 구조 조정은 선행되어 있다고 가정).
/// 목표: 모든 빌드 파워 38~62 (반격 부재 빌드 0).
/// </summary>
internal static class WeaponSweep
{
    private static readonly (string Wpn, string Tac)[] B =
    {
        ("WPN_SWORD", "TAC_BALANCED"), ("WPN_SPEAR", "TAC_COUNTER"), ("WPN_AXE", "TAC_BRAWLER"),
        ("WPN_GREATSWORD", "TAC_PRESSURE"), ("WPN_DUALBLADES", "TAC_BRAWLER"), ("WPN_HAMMER", "TAC_PRESSURE"),
        ("WPN_WHIP", "TAC_ZONER"), ("WPN_SHIELD", "TAC_DEFENDER"),
    };

    public static void Run(int games, int iters)
    {
        int n = B.Length;
        var scale = B.ToDictionary(b => b.Wpn, _ => 1.0f);
        var defs = B.Select(b => new FighterDef(b.Wpn[4..], FighterStats.Baseline, b.Wpn, b.Tac, "PER_CALM")).ToArray();
        var pairs = new List<(int i, int j)>();
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) pairs.Add((i, j));

        Console.WriteLine($"=== 무기 데미지 자동 스윕: 좌표하강, 칸당 {games}경기, 최대 {iters}회 ===\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        float[] powers = Measure(defs, scale, games, pairs, n);
        for (int it = 0; it < iters; it++)
        {
            int worst = 0; float worstDev = 0f;
            for (int k = 0; k < n; k++) { float d = MathF.Abs(powers[k] - 50f); if (d > worstDev) { worstDev = d; worst = k; } }
            if (worstDev < 12f) { Console.WriteLine($"[{it}] 수렴 — 최대 편차 {worstDev:F1}%p (전 빌드 38~62)"); break; }

            string w = B[worst].Wpn;
            scale[w] = Math.Clamp(scale[w] * (powers[worst] > 50f ? 0.95f : 1.05f), 0.5f, 1.8f);
            Console.WriteLine($"[{it}] 최악 {w[4..],-11} {powers[worst],5:F1}% → 데미지 배율 {scale[w]:F2}");
            powers = Measure(defs, scale, games, pairs, n);
        }
        sw.Stop();

        Console.WriteLine($"\n수렴 결과 ({sw.Elapsed.TotalSeconds:F0}초):");
        Console.WriteLine("  무기        배율   파워   제안 BaseDamage(현재→신규)");
        for (int i = 0; i < n; i++)
        {
            var wd = WeaponTable.Get(B[i].Wpn);
            float newBase = wd.BaseDamage * scale[B[i].Wpn];
            Console.WriteLine($"  {B[i].Wpn[4..],-11} {scale[B[i].Wpn],4:F2}  {powers[i],5:F1}  " +
                              $"{wd.BaseDamage,5:F0} → {newBase,5:F1}{(MathF.Abs(powers[i] - 50f) > 12f ? "  ◀ 미수렴(데미지만으론 한계)" : "")}");
        }
        Console.WriteLine($"\n반격 부재(>62%): {powers.Count(p => p > 62f)}/8, 과약(<38%): {powers.Count(p => p < 38f)}/8");
    }

    private static float[] Measure(FighterDef[] defs, Dictionary<string, float> scale, int games, List<(int i, int j)> pairs, int n)
    {
        var win = new int[n]; var dec = new int[n];
        object lk = new();
        Parallel.ForEach(pairs, p =>
        {
            int wi = 0, dc = 0;
            for (ulong s = 1; s <= (ulong)games; s++)
            {
                bool iFirst = (s & 1) == 1;
                int iIdx = iFirst ? 0 : 1;
                var r = new MatchSim(null, scale).Run(iFirst ? defs[p.i] : defs[p.j], iFirst ? defs[p.j] : defs[p.i], s);
                if (r.Winner != -1) { dc++; if (r.Winner == iIdx) wi++; }
            }
            lock (lk)
            {
                win[p.i] += wi; dec[p.i] += dc;
                win[p.j] += dc - wi; dec[p.j] += dc;
            }
        });
        var pow = new float[n];
        for (int i = 0; i < n; i++) pow[i] = dec[i] > 0 ? 100f * win[i] / dec[i] : 50f;
        return pow;
    }
}
