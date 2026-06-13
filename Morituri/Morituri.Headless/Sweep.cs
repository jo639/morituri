using System.Collections.Concurrent;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 파라미터 그리드 스윕 (M3-A). 8개 강결합 상수의 비선형계를 손튜닝하는 대신,
/// 헤드리스 성능(Parallel)으로 구성 공간을 탐색해 "동일 무기 전술 RPS가 상수 조정만으로
/// 성립 가능한가"를 검증한다. 목적함수 = 같은무기 매트릭스의 '건강한 칸' 최대화.
///   · 건강(healthy): 승률 18~82% 안 & |승률-50|≥6 (차별화 + 반격 여지 공존)
///   · 퇴화(degenerate): 승률 >82 또는 <18 (반격 불가 = 가위바위보 아님)
///   · 평탄(flat): |승률-50| <6 (전술 차이 없음)
/// </summary>
internal static class Sweep
{
    private static readonly string[] Tac =
        { "TAC_PRESSURE", "TAC_COUNTER", "TAC_ZONER", "TAC_BRAWLER", "TAC_DEFENDER" };

    // 탐색 축 (현재값 중심 ±). 가장 영향 큰 4종 × 3레벨 = 81구성.
    private static readonly float[] GuardedRec = { 1.2f, 1.65f, 2.1f };  // 가드 처벌(방어 강도)
    private static readonly float[] Whiff      = { 6f, 12f, 20f };       // 헛스윙 처벌(공격 위험)
    private static readonly float[] ExhaustDmg = { 1.0f, 1.3f, 1.8f };   // 지침 처벌
    private static readonly float[] Gate       = { 0.7f, 0.9f, 1.2f };   // 공격 채택 신중도

    private record struct Cfg(float Grec, float Whiff, float Exh, float Gate);
    private record struct Score(Cfg C, int Healthy, int Degenerate, int Flat, float Obj, string Worst);

    public static void Run(int gamesPerCell, string weaponId)
    {
        var grid = new List<Cfg>();
        foreach (var a in GuardedRec) foreach (var b in Whiff) foreach (var c in ExhaustDmg) foreach (var d in Gate)
            grid.Add(new Cfg(a, b, c, d));

        Console.WriteLine($"=== 파라미터 스윕: {grid.Count}구성 × 10매치업 × {gamesPerCell}경기 (무기 {weaponId.Replace("WPN_", "")}) ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new ConcurrentBag<Score>();

        Parallel.ForEach(grid, cfg =>
        {
            var bc = BalanceConstants.Default with
            {
                GuardedRecoveryMult = cfg.Grec,
                StamCostWhiff = cfg.Whiff,
                ExhaustDamageTakenMult = cfg.Exh,
                AttackGateScale = cfg.Gate,
            };

            int healthy = 0, degen = 0, flat = 0;
            float worstDev = 0; string worst = "";
            for (int i = 0; i < 5; i++)
            for (int j = i + 1; j < 5; j++)
            {
                float pct = PairWinPct(i, j, gamesPerCell, bc, weaponId);
                float devFrom50 = MathF.Abs(pct - 50f);
                if (pct > 82f || pct < 18f) { degen++; if (devFrom50 > worstDev) { worstDev = devFrom50; worst = $"{Tac[i][4..]}>{Tac[j][4..]} {pct:F0}%"; } }
                else if (devFrom50 < 6f) flat++;
                else healthy++;
            }
            float obj = healthy - 1.5f * degen;
            results.Add(new Score(cfg, healthy, degen, flat, obj, worst));
        });
        sw.Stop();

        var ranked = results.OrderByDescending(s => s.Obj).ThenBy(s => s.Flat).ToList();
        Console.WriteLine($"완료: {grid.Count * 10 * gamesPerCell}경기 / {sw.Elapsed.TotalSeconds:F0}초\n");
        Console.WriteLine("상위 12구성 (건강↑ 퇴화↓ 평탄↓):");
        Console.WriteLine("  GRec  Whiff  ExhD  Gate | 건강 퇴화 평탄  목적  최악퇴화칸");
        foreach (var s in ranked.Take(12))
            Console.WriteLine($"  {s.C.Grec,4:F2} {s.C.Whiff,5:F0} {s.C.Exh,5:F2} {s.C.Gate,5:F2} |" +
                              $" {s.Healthy,3} {s.Degenerate,4} {s.Flat,4} {s.Obj,6:F1}  {s.Worst}");

        var best = ranked[0];
        Console.WriteLine($"\n최고 구성 건강칸 {best.Healthy}/10. " +
            (best.Healthy >= 7 ? "상수 조정만으로 RPS 성립 가능 → 채택 검토." :
             best.Degenerate >= 4 ? "최적해도 퇴화 다수 → 동일무기 RPS는 상수만으론 불가 (전술=무기 결합 결론)." :
             "부분 성립 — 평탄(전술 미분화)이 한계."));
        Console.WriteLine($"분포: 최고 건강 {ranked.Max(s => s.Healthy)}, 최저 퇴화 {ranked.Min(s => s.Degenerate)}, " +
                          $"전 구성 평균 건강 {ranked.Average(s => s.Healthy):F1}/10");
    }

    private static float PairWinPct(int i, int j, int games, BalanceConstants bc, string weaponId)
    {
        var a = new FighterDef("a", FighterStats.Baseline, weaponId, Tac[i], "PER_CALM");
        var b = new FighterDef("b", FighterStats.Baseline, weaponId, Tac[j], "PER_CALM");
        int winI = 0, dec = 0;
        for (ulong seed = 1; seed <= (ulong)games; seed++)
        {
            bool iFirst = (seed & 1) == 1;
            int iIdx = iFirst ? 0 : 1;
            var r = new MatchSim(bc).Run(iFirst ? a : b, iFirst ? b : a, seed);
            if (r.Winner != -1) { dec++; if (r.Winner == iIdx) winI++; }
        }
        return dec > 0 ? 100f * winI / dec : 50f;
    }
}
