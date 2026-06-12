using System.Diagnostics;
using Morituri.Headless;
using Morituri.Sim.Data;
using Morituri.Sim.Match;

// MORITURI 헤드리스 배치 러너 (문서[4] 12장 / 문서[1] M3 초도판)
// 사용: dotnet run -- [N]                       배치 통계 (매치업당 N경기, 기본 1000)
//       dotnet run -- replay [매치업] [시드]     경기 한 판 텍스트 중계
//                     매치업: berserker(기본) | mirror | cruel | arrogant
//       dotnet run -- matrix [N]                상성 매트릭스 5×5 + matchup_report.csv (칸당 N경기, 기본 1000)

if (args.Length > 0 && args[0] == "matrix")
{
    int games = args.Length > 1 && int.TryParse(args[1], out int g) ? g : 1000;
    MatrixReport.Run(games, "matchup_report.csv");
    return;
}

if (args.Length > 0 && args[0] == "replay")
{
    string matchup = args.Length > 1 ? args[1] : "berserker";
    ulong seed = args.Length > 2 && ulong.TryParse(args[2], out ulong s) ? s : 1;
    bool verbose = args.Length > 3 && (args[3] == "v" || args[3] == "verbose");
    Replay.Run(matchup, seed, verbose);
    return;
}

int n = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 1000;

Console.WriteLine($"=== MORITURI 배치 시뮬레이션 (매치업당 {n}경기, 시드 1~{n}) ===\n");

// ── 1. 거울 매치 (밸런스 sanity: 동일 선수 → 50/50 기대) ──
RunMatchup("거울 검증: 균형형+검 vs 균형형+검",
    new FighterDef("A", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM"),
    new FighterDef("B", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM"), n);

// ── 2. 문서[4] 11장 필수 케이스 ──
RunMatchup("★ 기획 필수: 버서커(난전+충동+도끼) vs 전술가(카운터+냉철+창) — 목표: 전술가 55~60%",
    FighterDef.Berserker, FighterDef.Tactician, n);

// ── 3. 성격 가독성 케이스 (문서[3] 9장 체크 1) ──
RunMatchup("압박형+잔혹함(검) vs 압박형+겁쟁이(검)",
    new FighterDef("학살자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL"),
    new FighterDef("허당", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_COWARD"), n);

// ── 4. 오만함 도발 역전 케이스 (문서[3] 9장 체크 3) ──
RunMatchup("오만함 챔피언(압박+검) vs 도전자(균형+검) — 도발 후 역전패율 목표 5~10%",
    new FighterDef("챔피언", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT"),
    new FighterDef("도전자", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM"), n);

void RunMatchup(string title, FighterDef a, FighterDef b, int games)
{
    var sw = Stopwatch.StartNew();
    int winA = 0, winB = 0, draw = 0, ko = 0;
    int comebacks = 0;          // 승자가 HP 30% 이하까지 몰렸다 이김 (역전)
    int tauntMatches = 0, tauntLoss = 0;
    double durSum = 0;

    for (ulong seed = 1; seed <= (ulong)games; seed++)
    {
        var r = new MatchSim().Run(a, b, seed);
        if (r.Winner == 0) winA++; else if (r.Winner == 1) winB++; else draw++;
        if (r.Reason == "KO") ko++;
        durSum += r.DurationSec;

        if (r.Winner >= 0)
        {
            var w = r.Winner == 0 ? r.StatsA : r.StatsB;
            if (w.MinHpPct <= 0.30f) comebacks++;
            var l = r.Winner == 0 ? r.StatsB : r.StatsA;
            if (l.Taunted) tauntLoss++;
        }
        if (r.StatsA.Taunted || r.StatsB.Taunted) tauntMatches++;
    }
    sw.Stop();

    int decided = winA + winB;
    Console.WriteLine($"▶ {title}");
    Console.WriteLine($"   {a.Name} {Pct(winA, games)} | {b.Name} {Pct(winB, games)} | 무승부 {Pct(draw, games)}");
    Console.WriteLine($"   KO {Pct(ko, games)} / 판정 {Pct(games - ko - draw, games)} | 평균 경기시간 {durSum / games:F1}초");
    Console.WriteLine($"   역전승(HP30% 열세→승) {Pct(comebacks, Math.Max(1, decided))}" +
        (tauntMatches > 0 ? $" | 도발 발생 {Pct(tauntMatches, games)}, 도발자 패배 {Pct(tauntLoss, Math.Max(1, tauntMatches))} (도발 경기 중)" : ""));
    Console.WriteLine($"   ({games}경기 / {sw.ElapsedMilliseconds}ms = 경기당 {sw.Elapsed.TotalMilliseconds / games:F2}ms)\n");
}

static string Pct(int x, int total) => $"{100.0 * x / total:F1}%";
