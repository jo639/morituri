using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 도발 심리효과 측정: 오만 도발자 vs 성격별 상대. 도발 경기에서 도발자 패배율이
/// 성격별로 갈리면(충동↑·겁쟁이↓) A 분노 + C 성격반응이 실제로 결과를 바꾼다는 증거.
/// 비교군: 의협(도발 반응 트리거 없음 = 순수 A 기준선) / 충동(증폭) / 냉철(중화) / 겁쟁이(위축).
/// </summary>
static class TauntProbe
{
    public static void Run(int n)
    {
        var arrogant = new FighterDef("Arrogant", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT");
        (string Label, string Per)[] rivals =
        {
            ("고결함(A-only base)",    "PER_HONORABLE"),
            ("Reckless(amplify)",      "PER_RECKLESS"),
            ("Calm(cancel)",           "PER_CALM"),
            ("Coward(flinch)",         "PER_COWARD"),
        };

        Console.WriteLine($"=== Taunt psychological effect ({n:N0} games each, Arrogant taunter vs each personality) ===\n");
        Console.WriteLine($"{"Rival personality",-24} {"taunter win%",12} {"taunt occur%",13} {"taunter-lose% | in taunted games",34}");
        foreach (var (label, per) in rivals)
        {
            var rival = new FighterDef("Rival", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", per);
            int win0 = 0, tauntMatches = 0, tauntLoss = 0;
            for (ulong seed = 1; seed <= (ulong)n; seed++)
            {
                var r = new MatchSim().Run(arrogant, rival, seed);
                if (r.Winner == 0) win0++;
                if (r.StatsA.Taunted) { tauntMatches++; if (r.Winner == 1) tauntLoss++; }
            }
            float loseInTaunt = tauntMatches > 0 ? 100f * tauntLoss / tauntMatches : 0f;
            Console.WriteLine($"{label,-24} {100f * win0 / n,11:F1}% {100f * tauntMatches / n,12:F1}% {loseInTaunt,22:F1}%   (n={tauntMatches})");
        }
    }
}
