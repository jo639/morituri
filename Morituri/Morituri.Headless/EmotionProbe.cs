using Morituri.Sim.Core;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 감정(T10) 엔진 검증 프로브 (로드맵[0] Phase 2 합격기준: "감정 유무로 승률·행동 분포가 측정 가능하게 갈린다").
/// 동일 미러 매치에서 A에게만 감정을 주입해 무감정 대비 델타를 측정한다.
/// 감정은 decision-only이므로 변화는 '행동'(공격 빈도·거리·도발)에서 나온다 — 데미지 배율 아님.
/// </summary>
public static class EmotionProbe
{
    public static void Run(int games)
    {
        // 기준 빌드: 검·균형·충동 미러 — 트리거(분노/도발) 존재 + 중립 전술이라 감정 효과가 또렷하게 드러난다.
        const string wpn = "WPN_SWORD", tac = "TAC_BALANCED", per = "PER_RECKLESS";
        FighterDef Make(string name, string[]? emo) =>
            new(name, FighterStats.Baseline, wpn, tac, per, null, emo);

        Console.WriteLine($"=== 감정(T10) 프로브 — {wpn}/{tac}/{per} 미러, A에게만 감정 주입 (각 {games}경기) ===");
        Console.WriteLine("  감정 = 의사선택만 바꾼다(데미지 배율 아님). 무감정 대비 행동·승률 변화를 본다.\n");
        Console.WriteLine($"  {"감정",-9}{"A승률",9}{"KO%",8}{"A공격시도",11}{"A코너초",9}{"A도발%",8}");

        Report("(무감정)", null, Make, games);
        foreach (var e in EmotionTable.All)
            Report(e.Name, new[] { e.Id }, Make, games);
    }

    /// <summary>
    /// 감정 발생률(GenChance) 점검 — 전 성격쌍 × seedsPerPair 경기를 돌려, 결과마다 양쪽 선수의 감정을 Roll하고
    /// 실제 발생 빈도를 집계한다. "감정은 매 경기가 아니라 가끔 생기는 변화구"가 수치로 맞는지 본다.
    /// </summary>
    public static void GenRate(int seedsPerPair)
    {
        const string wpn = "WPN_SWORD", tac = "TAC_BALANCED";
        var pers = PersonalityTable.All;
        var tally = new Dictionary<string, int>();
        foreach (var e in EmotionTable.All) tally[e.Id] = 0;
        int samples = 0, anyEmotion = 0, draws = 0;

        foreach (var pa in pers)
            foreach (var pb in pers)
            {
                var a = new FighterDef("A", FighterStats.Baseline, wpn, tac, pa.Id);
                var b = new FighterDef("B", FighterStats.Baseline, wpn, tac, pb.Id);
                for (ulong s = 1; s <= (ulong)seedsPerPair; s++)
                {
                    var r = new MatchSim().Run(a, b, s);
                    bool ko = r.Reason == "KO";
                    if (r.Winner < 0) draws++;
                    for (int side = 0; side < 2; side++)
                    {
                        samples++;
                        float minHp = side == 0 ? r.StatsA.MinHpPct : r.StatsB.MinHpPct;
                        var rng = new SimRandom(s * 4 + (ulong)side + 1);
                        var id = EmotionGen.Roll(rng, r.Winner, side, ko, minHp, side == 0 ? pa : pb);
                        if (id != null) { tally[id]++; anyEmotion++; }
                    }
                }
            }

        int matches = pers.Length * pers.Length * seedsPerPair;
        Console.WriteLine($"=== 감정 발생률 점검 — 전 성격쌍({pers.Length}×{pers.Length}) × {seedsPerPair}시드 = {matches}경기, {samples}샘플(선수×경기) ===");
        Console.WriteLine($"  감정은 '가끔' 생기는 이벤트성 변화구 — 대부분의 결과는 무감정이어야 한다.\n");
        Console.WriteLine($"  감정 발생(전체): {100.0 * anyEmotion / samples,5:F1}%  |  무감정: {100.0 * (samples - anyEmotion) / samples,5:F1}%  (무승부 {100.0 * draws / matches:F1}%)\n");
        Console.WriteLine($"  {"감정",-9}{"GenChance",11}{"발생수",9}{"전체대비",10}");
        foreach (var e in EmotionTable.All)
            Console.WriteLine($"  {e.Name,-9}{e.GenChance,10:P0}{tally[e.Id],9}{100.0 * tally[e.Id] / samples,9:F1}%");
    }

    private static void Report(string label, string[]? emo, Func<string, string[]?, FighterDef> make, int games)
    {
        var a = make("A", emo);
        var b = make("B", null);
        int winA = 0, ko = 0, taunt = 0;
        double atk = 0, corner = 0;
        for (ulong s = 1; s <= (ulong)games; s++)
        {
            var r = new MatchSim().Run(a, b, s);
            if (r.Winner == 0) winA++;
            if (r.Reason == "KO") ko++;
            atk += r.StatsA.AttackAttempts;
            corner += r.StatsA.CornerTime;
            if (r.StatsA.Taunted) taunt++;
        }
        Console.WriteLine($"  {label,-9}{100.0 * winA / games,8:F1}%{100.0 * ko / games,7:F1}%{atk / games,11:F1}{corner / games,9:F1}{100.0 * taunt / games,7:F1}%");
    }
}
