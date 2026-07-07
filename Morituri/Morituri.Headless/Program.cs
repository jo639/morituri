using System.Diagnostics;
using Morituri.Headless;
using Morituri.Sim.Data;
using Morituri.Sim.Match;
using Morituri.Sim.Serialization;

// MORITURI 헤드리스 배치 러너 (문서[4] 12장 / 문서[1] M3 초도판)
// 사용: dotnet run -- [N]                       배치 통계 (매치업당 N경기, 기본 1000)
//       dotnet run -- replay [매치업] [시드]     경기 한 판 텍스트 중계
//                     매치업: berserker(기본) | mirror | cruel | arrogant
//                            | b:무기/전술/성격:무기/전술/성격  (빌드 직접 지정, 빈 필드=기본값, B생략=거울)
//                              예: b:WHIP/ZONER/SHOWMAN:AXE/BRAWLER/CRUEL  /  b:/PRESSURE/ARROGANT
//       dotnet run -- matrix [N]                상성 매트릭스 5×5 + matchup_report.csv (칸당 N경기, 기본 1000)
//       dotnet run -- export [매치업] [시드]    경기 1판 → match.json (MatchRecord, 역사 DB 입력)
//       dotnet run -- viewer [매치업] [시드]    경기 1판 → viewer.json (위치 프레임 포함, viewer.html이 재생)
//                     매치업은 replay와 동일 (b:무기/전술/성격 빌드 지정 가능)
//       dotnet run -- highlights [N]            명경기 자동 태깅 → highlights.json
//       dotnet run -- emotionprobe [N]          감정(T10) 엔진 검증 — 감정 유무 승률·행동 델타 (기본 500)
//       dotnet run -- emotiongen [N]            감정 발생률 점검 — 전 성격쌍 × N시드 감정 생성 빈도 (기본 30)
//       dotnet run -- relations [N]             관계(T11) 메타 데모 — 라운드로빈 누적 관계 그래프·복수전 후보 (기본 20)
//       dotnet run -- season [rounds] [seed] [fresh] [serve]  Phase 3 시즌 — 순위·관계·감정·명성·서사.
//                     world.json 영속(재실행=누적, fresh=초기화) · season.json 내보냄 · serve=league.html 대시보드 서버

if (args.Length > 0 && args[0] == "oddsprobe")
{
    // 배당 캘리브레이션: 시드 세계의 캐스트로 예측식 후보 비교
    Directory.SetCurrentDirectory(Path.GetTempPath());
    ulong ws = args.Length > 1 && ulong.TryParse(args[1], out ulong opw) ? opw : 777;
    int sims = args.Length > 2 && int.TryParse(args[2], out int ops) ? ops : 80;
    new Game(1, ws, fresh: true, interactive: false, playerless: true).OddsProbe(sims);
    return;
}

if (args.Length > 0 && args[0] == "health")
{
    HealthCheck.Run(
        args.Length > 1 && int.TryParse(args[1], out int hs) ? hs : 5,
        args.Length > 2 && int.TryParse(args[2], out int hn) ? hn : 50);
    return;
}

if (args.Length > 0 && args[0] == "matrix")
{
    int games = args.Length > 1 && int.TryParse(args[1], out int g) ? g : 1000;
    string wpn = args.Length > 2 ? (args[2].StartsWith("WPN_") ? args[2] : "WPN_" + args[2].ToUpper()) : "WPN_SWORD";
    MatrixReport.Run(games, "matchup_report.csv", wpn);
    return;
}

if (args.Length > 0 && args[0] == "weaponprobe")
{
    Analysis.WeaponProbe(args.Length > 1 && int.TryParse(args[1], out int wg) ? wg : 300);
    return;
}

if (args.Length > 0 && args[0] == "sigmatrix")
{
    Analysis.SignatureMatrix(args.Length > 1 && int.TryParse(args[1], out int sg) ? sg : 500);
    return;
}

if (args.Length > 0 && args[0] == "parryprobe")
{
    Analysis.ParryProbe(args.Length > 1 && int.TryParse(args[1], out int pp) ? pp : 300);
    return;
}

if (args.Length > 0 && args[0] == "emotionprobe")
{
    EmotionProbe.Run(args.Length > 1 && int.TryParse(args[1], out int ep) ? ep : 500);
    return;
}

if (args.Length > 0 && args[0] == "emotiongen")
{
    EmotionProbe.GenRate(args.Length > 1 && int.TryParse(args[1], out int eg) ? eg : 30);
    return;
}

if (args.Length > 0 && args[0] == "relations")
{
    RelationProbe.Run(args.Length > 1 && int.TryParse(args[1], out int rl) ? rl : 20);
    return;
}

if (args.Length > 0 && args[0] == "season")
{
    int rounds = args.Length > 1 && int.TryParse(args[1], out int sr) ? sr : 6;
    ulong sseed = args.Length > 2 && ulong.TryParse(args[2], out ulong ss) ? ss : 1;
    bool fresh = args.Any(a => a == "fresh");   // world.json 무시하고 새 세계 시작
    bool serve = args.Any(a => a == "serve");   // season.json 내보내고 league.html 서버 기동
    Game.RunCli(rounds, sseed, fresh, serve);   // W1: Season.Run을 상태 기계 Game이 흡수
    return;
}

if (args.Length > 0 && args[0] == "tune")
{
    Tune.Run(args.Length > 1 && int.TryParse(args[1], out int tg) ? tg : 100);
    return;
}

if (args.Length > 0 && args[0] == "tunetac")
{
    int ttg = args.Length > 1 && int.TryParse(args[1], out int a1) ? a1 : 60;
    int ttp = args.Length > 2 && int.TryParse(args[2], out int a2) ? a2 : 2;
    Tune.RunDescent(ttg, ttp);
    return;
}

if (args.Length > 0 && args[0] == "spacingprobe")
{
    Analysis.SpacingProbe(args.Length > 1 && int.TryParse(args[1], out int spp) ? spp : 20);
    return;
}

if (args.Length > 0 && args[0] == "statgen")
{
    StatGenReport.Run(args.Length > 1 && int.TryParse(args[1], out int sgn) ? sgn : 20000);
    return;
}

if (args.Length > 0 && args[0] == "taunt")
{
    TauntProbe.Run(args.Length > 1 && int.TryParse(args[1], out int tn) ? tn : 8000);
    return;
}

if (args.Length > 0 && args[0] == "tauntfreq")
{
    TauntFrequencyProbe.Run(args.Length > 1 && int.TryParse(args[1], out int tfn) ? tfn : 3000);
    return;
}

if (args.Length > 0 && args[0] == "weaponsweep")
{
    int wsg = args.Length > 1 && int.TryParse(args[1], out int a1) ? a1 : 150;
    int wsi = args.Length > 2 && int.TryParse(args[2], out int a2) ? a2 : 30;
    WeaponSweep.Run(wsg, wsi);
    return;
}

if (args.Length > 0 && args[0] == "buildmatrix")
{
    Analysis.WeaponBuildMatrix(args.Length > 1 && int.TryParse(args[1], out int bmg) ? bmg : 500);
    return;
}

if (args.Length > 0 && args[0] == "weaponbalance")
{
    string wbTac = args.Length > 2 ? (args[2].StartsWith("TAC_") ? args[2] : "TAC_" + args[2].ToUpper()) : "TAC_BALANCED";
    Analysis.WeaponBalance(args.Length > 1 && int.TryParse(args[1], out int wbg) ? wbg : 400, wbTac);
    return;
}

if (args.Length > 0 && args[0] == "sweep")
{
    int sgames = args.Length > 1 && int.TryParse(args[1], out int spg) ? spg : 120;
    string sweepWpn = args.Length > 2 ? (args[2].StartsWith("WPN_") ? args[2] : "WPN_" + args[2].ToUpper()) : "WPN_SWORD";
    Sweep.Run(sgames, sweepWpn);
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

if (args.Length > 0 && args[0] == "export")
{
    // 경기 한 판을 JSON으로 (M4 뷰어·Phase 3 역사 DB 입력). 매치업 어휘는 replay와 동일.
    string matchup = args.Length > 1 ? args[1] : "berserker";
    ulong seed = args.Length > 2 && ulong.TryParse(args[2], out ulong es) ? es : 1;
    string outPath = args.Length > 3 ? args[3] : "match.json";
    var (ea, eb) = Replay.Pick(matchup);
    File.WriteAllText(outPath, MatchSerializer.Serialize(MatchSerializer.Capture(ea, eb, seed)));
    Console.WriteLine($"{ea.Name} vs {eb.Name} (시드 {seed}) → {outPath}");
    return;
}

if (args.Length > 0 && args[0] == "highlights")
{
    int hg = args.Length > 1 && int.TryParse(args[1], out int hgp) ? hgp : 1000;
    string outPath = args.Length > 2 ? args[2] : "highlights.json";
    Highlights.Collect(hg, outPath);
    return;
}

if (args.Length > 0 && args[0] == "viewer")
{
    // dotnet run -- viewer [매치업] [경기시드] [천부시드A] [천부시드B]
    // 천부시드 생략 시 천부·잠재력 정보 없이 생성 (기존 동작 유지)
    string matchup = args.Length > 1 ? args[1] : "berserker";
    ulong seed = args.Length > 2 && ulong.TryParse(args[2], out ulong vs) ? vs : 1;
    ulong? talentA = args.Length > 3 && ulong.TryParse(args[3], out ulong ta) ? ta : null;
    ulong? talentB = args.Length > 4 && ulong.TryParse(args[4], out ulong tb) ? tb : talentA; // B 생략 시 A 시드 재사용
    string outPath = "viewer.json";
    var (va, vb) = Replay.Pick(matchup);
    ViewerExport.Run(va, vb, seed, outPath, talentA, talentB);
    ViewerServer.Serve(Directory.GetCurrentDirectory());
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
// near-mirror: 도발(오만함)만이 유일한 차이 → 50:50 기준선에서 도발의 '비용'을 격리 측정.
// 전술 불균형 매치업은 상성이 도발 효과를 삼켜 역전이 수학적으로 불가능했다 (M3-B 진단).
RunMatchup("오만함 챔피언(압박+검+오만) vs 도전자(압박+검+냉철) — 도발 후 역전패율 목표 5~10%",
    new FighterDef("챔피언", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT"),
    new FighterDef("도전자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CALM"), n);

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
