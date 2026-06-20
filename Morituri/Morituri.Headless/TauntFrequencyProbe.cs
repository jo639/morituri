using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 성격별 도발 빈도 측정. 모든 성격이 동일 무기·전술로 동일 중립 상대와 싸울 때
/// 경기당 평균 도발 횟수를 측정 — 설계 빈도 순위(오만·쇼맨 최상위, 겁쟁이·신중 최하위)가
/// 실측에서도 나타나는지 검증한다.
/// 상대: WPN_SWORD / TAC_BALANCED / PER_CALM (중립 기준선)
/// 테스터: WPN_SWORD / TAC_BALANCED / 각 성격
/// </summary>
static class TauntFrequencyProbe
{
    record PersonalityRow(string KorName, string Per);

    static readonly PersonalityRow[] Personalities =
    {
        new("쇼맨",       "PER_SHOWMAN"),
        new("오만함",     "PER_ARROGANT"),
        new("잔혹함",     "PER_CRUEL"),
        new("충동적",     "PER_RECKLESS"),
        new("대담함",     "PER_BOLD"),
        new("냉철함",     "PER_CALM"),
        new("기회주의자", "PER_OPPORTUNIST"),
        new("고결함",     "PER_HONORABLE"),
        new("신중함",     "PER_WARY"),
        new("겁쟁이",     "PER_COWARD"),
    };

    public static void Run(int n)
    {
        var neutral = new FighterDef("중립", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM");

        Console.WriteLine($"=== 성격별 도발 빈도 ({n:N0}경기 × 10성격, 중립 상대: TAC_BALANCED/PER_CALM) ===\n");
        Console.WriteLine($"{"성격",-10} {"평균/경기",8} {"최대",6} {"도발 발생 경기%",16} {"트리거 조건 메모"}");
        Console.WriteLine(new string('-', 72));

        foreach (var row in Personalities)
        {
            var tester = new FighterDef(row.KorName, FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", row.Per);
            int totalTaunts = 0, maxInGame = 0, gamesWithTaunt = 0;

            for (ulong seed = 1; seed <= (ulong)n; seed++)
            {
                var ev = new List<SimEvent>();
                new MatchSim().Run(tester, neutral, seed, ev);
                int cnt = ev.Count(e => e is Decision d && d.ReasonTag == "TAUNT"
                                                        && d.SourceLayer == "Execution"
                                                        && d.FighterId == 0);
                totalTaunts += cnt;
                if (cnt > maxInGame) maxInGame = cnt;
                if (cnt > 0) gamesWithTaunt++;
            }

            float avg = (float)totalTaunts / n;
            float pct = 100f * gamesWithTaunt / n;
            string memo = TriggerMemo(row.Per);
            Console.WriteLine($"{row.KorName,-10} {avg,8:F3} {maxInGame,6} {pct,14:F1}%  {memo}");
        }

        Console.WriteLine($"\n총 경기: {n * Personalities.Length:N0} | 도발 = FighterId 0(테스터)의 Execution/TAUNT 이벤트");
    }

    static string TriggerMemo(string per) => per switch
    {
        "PER_SHOWMAN"    => "다운조롱 0.55/cd6s + 피니시(HP≤25%) 0.50/cd30s",
        "PER_ARROGANT"   => "스태거+리드≥8% 0.70/cd15s + 상대HP≤50% 0.50/cd45s",
        "PER_CRUEL"      => "상대HP≤30%             prob 0.40  cd 30s",
        "PER_RECKLESS"   => "연속피격≥3회            prob 0.45  cd 12s",
        "PER_BOLD"       => "HP열세≥20%p            prob 0.22  cd 15s",
        "PER_CALM"       => "우세(HP≥65%+winning)   prob 0.22×0.5  cd 15s",
        "PER_OPPORTUNIST"=> "상대가드≤25%           prob 0.15  cd 15s",
        "PER_HONORABLE"  => "우세(HP≥80%+winning)   prob 0.12  cd 20s",
        "PER_WARY"       => "우세(HP≥90%+winning)   prob 0.07  cd 25s",
        "PER_COWARD"     => "우세(HP≥90%+winning)   prob 0.07  cd 20s",
        _ => "",
    };
}
