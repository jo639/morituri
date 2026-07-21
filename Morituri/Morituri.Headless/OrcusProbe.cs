using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// [13a] 부록 E-4 게이트 — 프롤로그 「AUC 661」이 성립하는 시드가 실재하는지 실증한다.
///
/// 요구 조건(전부 만족해야 프롤로그로 쓸 수 있다):
///   ① 오르쿠스(도끼)가 이긴다            — 이 장면은 "이기고 죽는" 순교여야 한다
///   ② 경기가 길다                        — 탑방패가 시간을 끌어 스스로 숨 막히게 하는 대진
///   ③ 도끼의 스태미나가 눈에 띄게 마른다 — "5분 시한부"의 유일한 시각적 근거
///   ④ 3~5분 안에 들어온다                — 프롤로그 길이
///
/// 실패 시(=조건을 만족하는 시드가 희소하면) 시나리오가 아니라 구현 방식을 바꾼다(스크립트 재생).
/// Sim 무접촉: 기존 MatchSim을 읽기만 한다. 로스터 밖 1회성 대진이라 매트릭스 산출에 미포함.
/// </summary>
internal static class OrcusProbe
{
    /// <summary>오르쿠스 — 도끼. 공격은 여전히 최고지만 몸이 오래 못 간다(HP·기동을 낮춰 "짧게 끝내야 사는" 몸으로).</summary>
    private static FighterDef Orcus() => new("오르쿠스",
        new FighterStats(Atk: 128f, Def: 96f, HpMax: 620f, Spd: 96f, Aspd: 104f, Rct: 100f),
        "WPN_AXE", "TAC_PRESSURE", "PER_CRUEL");

    /// <summary>스쿠타투스 — 탑방패. 죽이러 온 게 아니라 기다리러 왔다.</summary>
    private static FighterDef Scutatus() => new("스쿠타투스",
        new FighterStats(Atk: 62f, Def: 132f, HpMax: 820f, Spd: 88f, Aspd: 82f, Rct: 104f),
        "WPN_SHIELD", "TAC_DEFENDER", "PER_WARY");

    public static void Run(int seeds)
    {
        Console.WriteLine("=== [13a] 프롤로그 시드 탐색 — 오르쿠스(도끼) vs 스쿠타투스(탑방패) ===");
        Console.WriteLine($"    시드 1~{seeds} · 조건: 도끼 승리 + 장기전 + 스태미나 고갈\n");
        Baseline(Math.Min(seeds, 400));
        Sweep(Math.Min(seeds, 400));
        Console.WriteLine();
    }

    /// <summary>교착 해부 — 수비형끼리 180초를 채울 때 실제로 무슨 일이 일어나는가.
    /// "아예 안 친다"(개전 실패)인지 "치는데 다 막힌다"(관통 실패)인지에 따라 처방이 정반대다.</summary>
    public static void Stall(int seeds, string weapon = "WPN_SWORD")
    {
        Console.WriteLine($"=== 교착 해부: {weapon.Replace("WPN_", "")} · 수비형 조합 ===");
        Console.WriteLine("  A전술      B전술      평균초 타임아웃  A시도  A명중  A막힘  A가한피해  A남은HP");
        foreach (var (ta, tb) in new[] {
            ("TAC_DEFENDER", "TAC_DEFENDER"), ("TAC_DEFENDER", "TAC_COUNTER"),
            ("TAC_COUNTER",  "TAC_COUNTER"),  ("TAC_PRESSURE", "TAC_DEFENDER") })
        {
            var a = new FighterDef("A", FighterStats.Baseline, weapon, ta, "PER_CALM");
            var b = new FighterDef("B", FighterStats.Baseline, weapon, tb, "PER_CALM");
            double dur = 0, att = 0, hit = 0, blk = 0, dmg = 0, hp = 0; int to = 0;
            for (ulong s = 1; s <= (ulong)seeds; s++)
            {
                var r = new MatchSim().Run(a, b, s, null);
                dur += r.DurationSec; if (r.DurationSec >= 179.5f) to++;
                att += r.StatsA.AttackAttempts; hit += r.StatsA.CleanHits;
                blk += r.StatsB.Blocks; dmg += r.StatsA.DamageDealt; hp += r.StatsA.HpRemainPct;
            }
            Console.WriteLine($"  {ta.Replace("TAC_",""),-10} {tb.Replace("TAC_",""),-10} " +
                $"{dur/seeds,6:F0} {100.0*to/seeds,7:F0}% {att/seeds,6:F0} {hit/seeds,6:F0} {blk/seeds,6:F0} " +
                $"{dmg/seeds,9:F0} {100.0*hp/seeds,7:F0}%");
        }
        Console.WriteLine();
    }

    /// <summary>대조군 — **양측 완전 동일 기본 스탯**으로 무기·전술만 다르게. 프롤로그 탐색의 스탯은 각본이 준 것이라
    /// "엔진이 원래 그런가"를 이걸로 가른다. 여기서도 방어형이 안 지면 스탯이 아니라 동역학 문제다.</summary>
    private static void Baseline(int seeds)
    {
        Console.WriteLine("── 대조군: 기본 스탯 동일, 무기·전술만 다름 (도끼 vs 방패) ──");
        Console.WriteLine("  도끼전술     방패전술     도끼승률  무승부  평균초  타임아웃");
        foreach (string atac in new[] { "TAC_PRESSURE", "TAC_BRAWLER", "TAC_COUNTER" })
        foreach (string dtac in new[] { "TAC_DEFENDER", "TAC_COUNTER" })
        {
            var axe = new FighterDef("도끼", FighterStats.Baseline, "WPN_AXE", atac, "PER_CALM");
            var shd = new FighterDef("방패", FighterStats.Baseline, "WPN_SHIELD", dtac, "PER_CALM");
            int win = 0, draw = 0, dec = 0, timeout = 0; double dur = 0;
            for (ulong s = 1; s <= (ulong)seeds; s++)
            {
                bool axeFirst = (s & 1) == 1;                       // 코너 교대(선공 편향 상쇄)
                var r = new MatchSim().Run(axeFirst ? axe : shd, axeFirst ? shd : axe, s, null);
                dur += r.DurationSec;
                if (r.DurationSec >= 179.5f) timeout++;
                if (r.Winner == -1) { draw++; continue; }
                dec++;
                if (r.Winner == (axeFirst ? 0 : 1)) win++;
            }
            Console.WriteLine($"  {atac.Replace("TAC_",""),-12} {dtac.Replace("TAC_",""),-12} " +
                $"{100.0 * win / Math.Max(1, dec),7:F1}% {100.0 * draw / seeds,6:F0}% {dur / seeds,7:F0} {100.0 * timeout / seeds,8:F0}%");
        }
        Console.WriteLine();
    }

    /// <summary>기본 설정으로 도끼가 못 이기면 장면이 성립하지 않는다 — 어떤 조합이라야 "이기고 죽는지" 찾는다.
    /// 여기서 아무 조합도 안 나오면 시나리오가 아니라 구현 방식을 바꾼다(부록 E-4 대안).</summary>
    private static void Sweep(int seeds)
    {
        Console.WriteLine("── 설정 스윕(도끼 공격력 × 방패 방어력 × 도끼 전술) ──");
        Console.WriteLine("  도끼ATK 방패DEF 전술        승률   평균초  탈진율  조건충족");
        foreach (float atk in new[] { 128f, 150f, 175f, 200f })
        foreach (float sdef in new[] { 132f, 110f, 95f })
        foreach (string tac in new[] { "TAC_PRESSURE", "TAC_BRAWLER" })
        {
            var axe = new FighterDef("오르쿠스",
                new FighterStats(atk, 96f, 620f, 96f, 104f, 100f), "WPN_AXE", tac, "PER_CRUEL");
            var shd = new FighterDef("스쿠타투스",
                new FighterStats(62f, sdef, 820f, 88f, 82f, 104f), "WPN_SHIELD", "TAC_DEFENDER", "PER_WARY");

            int wins = 0, dec = 0, gaspRuns = 0, hit = 0; double dur = 0;
            var ev = new List<Morituri.Sim.Events.SimEvent>(2048);
            for (ulong s = 1; s <= (ulong)seeds; s++)
            {
                ev.Clear();
                var r = new MatchSim().Run(axe, shd, s, ev);
                dur += r.DurationSec;
                if (r.Winner == -1) continue;
                dec++;
                int gasps = ev.OfType<Morituri.Sim.Events.StaminaExhausted>().Count(e => e.FighterId == 0);
                if (gasps > 0) gaspRuns++;
                // "방패를 기어이 쪼갰다" = 방패의 가드 붕괴. 판정승이어도 이 그림이 나오면 장면은 성립한다.
                int broke = ev.OfType<Morituri.Sim.Events.GuardBroken>().Count(e => e.FighterId == 1);
                if (r.Winner == 0) { wins++; if (r.DurationSec >= 60f && gasps >= 2 && broke > 0) hit++; }
            }
            Console.WriteLine($"  {atk,6:F0} {sdef,6:F0} {tac.Replace("TAC_", ""),-11} " +
                $"{100.0 * wins / Math.Max(1, dec),5:F1}% {dur / seeds,6:F0} " +
                $"{100.0 * gaspRuns / Math.Max(1, dec),5:F0}% {hit,6}");
        }
    }

    private static void RunDetail(int seeds)
    {

        int axeWins = 0, decided = 0, ko = 0, exhaustedRuns = 0;
        double durSum = 0, durMax = 0;
        var hits = new List<(ulong Seed, float Dur, int Gasps, float FirstGasp, string Reason)>();
        var events = new List<Morituri.Sim.Events.SimEvent>(2048);

        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            events.Clear();
            var r = new MatchSim().Run(Orcus(), Scutatus(), seed, events);
            durSum += r.DurationSec;
            durMax = Math.Max(durMax, r.DurationSec);
            if (r.Winner == -1) continue;
            decided++;

            // 도끼가 숨이 차는 순간들 — "5분 시한부"의 시각적 근거
            var gasps = events.OfType<Morituri.Sim.Events.StaminaExhausted>().Where(e => e.FighterId == 0).ToList();
            if (gasps.Count > 0) exhaustedRuns++;

            if (r.Winner != 0) continue;       // 도끼 패배 — 이 장면이 성립하지 않는다
            axeWins++;
            if (r.Reason == "KO") ko++;

            // ① 승리 ② 장기전 ③ 숨이 차는 게 보인다
            if (r.DurationSec >= 60f && gasps.Count >= 2)
                hits.Add((seed, r.DurationSec, gasps.Count, gasps[0].Time, r.Reason));
        }

        Console.WriteLine($"판정 {decided}경기 · 도끼 승률 {100.0 * axeWins / Math.Max(1, decided):F1}% " +
                          $"(KO {100.0 * ko / Math.Max(1, axeWins):F0}%)");
        Console.WriteLine($"평균 {durSum / seeds:F1}초 · 최장 {durMax:F1}초 · 도끼가 숨이 찬 경기 {100.0 * exhaustedRuns / Math.Max(1, decided):F0}%\n");

        if (hits.Count == 0)
        {
            Console.WriteLine("!! 조건 충족 시드 없음 — 프롤로그를 실제 시뮬로 재생할 수 없다.");
            Console.WriteLine("   설계서 부록 E-4의 대안(사전 기록 이벤트 로그 재생)으로 전환할 것.");
            return;
        }

        Console.WriteLine($"조건 충족 {hits.Count}개 ({100.0 * hits.Count / Math.Max(1, decided):F1}%) — 상위 후보(장기전 순):");
        foreach (var h in hits.OrderByDescending(x => x.Dur).Take(12))
            Console.WriteLine($"  seed {h.Seed,6} · {h.Dur,5:F1}초 · 탈진 {h.Gasps}회(첫 {h.FirstGasp:F0}초) · {h.Reason}");
    }
}
