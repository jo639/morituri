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
