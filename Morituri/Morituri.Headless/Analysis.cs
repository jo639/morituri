using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 전술×무기 결합 분석 (M3-A 모델 검증).
/// "난전형이 검에서 약한 것은 버그인가, 무기 의존성의 발현인가?"를 데이터로 가른다.
/// 같은 무기 매트릭스(MatrixReport)는 '무기 중립 전제'라, 무기 결합 전술(난전/견제)의 정체성을
/// 구조적으로 표현하지 못한다 — 이 모듈이 그 사각을 메운다.
/// </summary>
internal static class Analysis
{
    // 전술 5종과 각자의 '시그니처 무기'(기획시안/문서[4] 의도).
    private static readonly (string Id, string Name, string SigWeapon)[] T =
    {
        ("TAC_PRESSURE", "압박",   "WPN_SWORD"),       // 중립 근접 압박
        ("TAC_COUNTER",  "카운터", "WPN_SPEAR"),       // 전술가+창 (문서[4])
        ("TAC_ZONER",    "견제",   "WPN_WHIP"),        // 최장 사거리 순수 견제
        ("TAC_BRAWLER",  "난전",   "WPN_DUALBLADES"),  // 근접 난전 특화 (기획시안: 쌍검)
        ("TAC_DEFENDER", "방어",   "WPN_SHIELD"),      // 방패 방어·CC 특화
    };

    private static readonly string[] AllWeapons =
    {
        "WPN_SWORD", "WPN_SPEAR", "WPN_AXE", "WPN_GREATSWORD",
        "WPN_DUALBLADES", "WPN_HAMMER", "WPN_WHIP", "WPN_SHIELD",
    };

    /// <summary>코너 교대 듀얼. 이벤트 미수집(속도). 행 승수/판정수 반환.</summary>
    private static (int winRow, int decided) Duel(FighterDef row, FighterDef col, int games, BalanceConstants? c)
    {
        int winRow = 0, decided = 0;
        for (ulong seed = 1; seed <= (ulong)games; seed++)
        {
            bool rowFirst = (seed & 1) == 1;
            int rowIdx = rowFirst ? 0 : 1;
            var r = new MatchSim(c).Run(rowFirst ? row : col, rowFirst ? col : row, seed);
            if (r.Winner != -1) { decided++; if (r.Winner == rowIdx) winRow++; }
        }
        return (winRow, decided);
    }

    /// <summary>한 전술이 각 무기를 들었을 때, 검+냉철함 5전술 필드 상대 평균 승률.</summary>
    private static float WeaponVsField(string tacticId, string weaponId, int games, BalanceConstants? c)
    {
        long win = 0, dec = 0;
        var probe = new FighterDef("probe", FighterStats.Baseline, weaponId, tacticId, "PER_CALM");
        foreach (var (oppId, _, _) in T)
        {
            var opp = new FighterDef("field", FighterStats.Baseline, "WPN_SWORD", oppId, "PER_CALM");
            var (w, d) = Duel(probe, opp, games, c);
            win += w; dec += d;
        }
        return dec > 0 ? 100f * win / dec : 50f;
    }

    /// <summary>전술×무기 히트맵: 무기 의존성을 한눈에. 검 필드 상대 평균 승률.</summary>
    public static void WeaponProbe(int games)
    {
        Console.WriteLine($"=== 전술×무기 히트맵: 각 (전술,무기)의 '검+냉철함 5전술 필드' 상대 평균 승률 % ===");
        Console.WriteLine($"    (칸당 {games}경기 × 5상대, 코너 교대)\n");

        Console.Write("전술＼무기 ");
        foreach (var w in AllWeapons) Console.Write($"{Short(w),7}");
        Console.WriteLine("   시그니처");

        foreach (var (id, name, sig) in T)
        {
            Console.Write($"{name,-7} ");
            float swordWin = 0, sigWin = 0;
            foreach (var w in AllWeapons)
            {
                float p = WeaponVsField(id, w, games, null);
                if (w == "WPN_SWORD") swordWin = p;
                if (w == sig) sigWin = p;
                Console.Write($"{p,6:F1}{(w == sig ? "*" : " ")}");
            }
            Console.WriteLine($"   {Short(sig)} {sigWin:F0} (검 {swordWin:F0}, Δ{sigWin - swordWin:+0;-0})");
        }
        Console.WriteLine("\n* = 시그니처 무기. Δ = 시그니처 − 검. 큰 양수 = 무기 의존 전술.");
    }

    /// <summary>시그니처 무기 매트릭스: 각 전술이 의도된 무기를 들었을 때의 5×5 상성.</summary>
    public static void SignatureMatrix(int games)
    {
        Console.WriteLine($"=== 시그니처 무기 매트릭스: 각 전술 + 의도 무기, 칸당 {games}경기 ===");
        Console.Write("        ");
        foreach (var t in T) Console.Write($"{t.Name,8}");
        Console.WriteLine("   평균");

        var defs = T.Select(t => new FighterDef(t.Name, FighterStats.Baseline, t.SigWeapon, t.Id, "PER_CALM")).ToArray();
        for (int i = 0; i < 5; i++)
        {
            Console.Write($"{T[i].Name,6}  ");
            float rowSum = 0; int rowCells = 0;
            for (int j = 0; j < 5; j++)
            {
                if (i == j) { Console.Write($"{"—",7} "); continue; }
                var (w, d) = Duel(defs[i], defs[j], games, null);
                float pct = d > 0 ? 100f * w / d : 50f;
                rowSum += pct; rowCells++;
                Console.Write($"{pct,6:F1}  ");
            }
            Console.WriteLine($" {rowSum / rowCells,5:F1}  ({T[i].SigWeapon.Replace("WPN_", "")})");
        }
        Console.WriteLine("\n각 전술이 '제 무기'를 들었을 때의 전술 간 상성 — 실제 플레이어가 필드하는 빌드.");
    }

    // 각 무기의 시그니처 빌드 (무기 → 자연스러운 전술). 성격은 냉철 고정(구조 2축 분리).
    private static readonly (string Wpn, string Tac, string Short)[] Builds =
    {
        ("WPN_SWORD",       "TAC_BALANCED", "검·균형"),
        ("WPN_SPEAR",       "TAC_COUNTER",  "창·카운터"),
        ("WPN_AXE",         "TAC_BRAWLER",  "도끼·난전"),
        ("WPN_GREATSWORD",  "TAC_PRESSURE", "대검·압박"),
        ("WPN_DUALBLADES",  "TAC_BRAWLER",  "쌍검·난전"),
        ("WPN_HAMMER",      "TAC_PRESSURE", "망치·압박"),
        ("WPN_WHIP",        "TAC_ZONER",    "채찍·견제"),
        ("WPN_SHIELD",      "TAC_DEFENDER", "방패·방어"),
    };

    /// <summary>무기×빌드 매트릭스 (M3-A2 신규 성공 지표). 각 무기를 '제 빌드'에서 8×8.
    /// 균형형 고정 측정(WeaponBalance)을 대체 — 무기는 빌드 결합이므로 제 빌드에서 재야 한다.</summary>
    public static void WeaponBuildMatrix(int games)
    {
        Console.WriteLine($"=== 무기×빌드 매트릭스: 각 무기+시그니처 전술+냉철, 칸당 {games}경기 ===");
        Console.Write("            ");
        foreach (var b in Builds) Console.Write($"{b.Short,9}");
        Console.WriteLine("   평균");

        var defs = Builds.Select(b => new FighterDef(b.Short, FighterStats.Baseline, b.Wpn, b.Tac, "PER_CALM")).ToArray();
        var avgs = new (string s, float a)[Builds.Length];
        for (int i = 0; i < Builds.Length; i++)
        {
            Console.Write($"{Builds[i].Short,-11} ");
            float sum = 0; int cells = 0;
            for (int j = 0; j < Builds.Length; j++)
            {
                if (i == j) { Console.Write($"{"—",9}"); continue; }
                var (w, d) = Duel(defs[i], defs[j], games, null);
                float pct = d > 0 ? 100f * w / d : 50f;
                sum += pct; cells++;
                Console.Write($"{pct,8:F1} ");
            }
            avgs[i] = (Builds[i].Short, sum / cells);
            Console.WriteLine($"  {sum / cells,5:F1}");
        }
        Console.WriteLine("\n빌드 파워 서열:");
        foreach (var (s, a) in avgs.OrderByDescending(x => x.a))
            Console.WriteLine($"   {s,-10} {a,5:F1}{(a > 62 ? "  ◀ 과강(모두 이김 위험)" : a < 38 ? "  ◀ 과약" : "")}");
        int noCounter = avgs.Count(x => x.a > 62);
        Console.WriteLine($"\n반격 부재(>62% = 모든 상대에 우위) 빌드: {noCounter}/8  (가위바위보 목표: 0)");
    }

    /// <summary>무기 파워 서열: 전술·성격(냉철) 고정, 무기 8종 8×8. 순수 무기 강도 분리.</summary>
    public static void WeaponBalance(int games, string tacticId = "TAC_BALANCED")
    {
        Console.WriteLine($"=== 무기 파워 매트릭스: {tacticId[4..]}+냉철함 고정, 무기 8종, 칸당 {games}경기 ===");
        Console.Write("        ");
        foreach (var w in AllWeapons) Console.Write($"{Short(w),6}");
        Console.WriteLine("   평균(파워)");

        var rows = new (string w, float avg)[AllWeapons.Length];
        for (int i = 0; i < AllWeapons.Length; i++)
        {
            var row = new FighterDef("a", FighterStats.Baseline, AllWeapons[i], tacticId, "PER_CALM");
            Console.Write($"{Short(AllWeapons[i]),6}  ");
            float sum = 0; int cells = 0;
            for (int j = 0; j < AllWeapons.Length; j++)
            {
                if (i == j) { Console.Write($"{"—",6}"); continue; }
                var col = new FighterDef("b", FighterStats.Baseline, AllWeapons[j], "TAC_BALANCED", "PER_CALM");
                var (w, d) = Duel(row, col, games, null);
                float pct = d > 0 ? 100f * w / d : 50f;
                sum += pct; cells++;
                Console.Write($"{pct,6:F1}");
            }
            float avg = sum / cells;
            rows[i] = (Short(AllWeapons[i]), avg);
            Console.WriteLine($"   {avg,5:F1}");
        }

        Console.WriteLine("\n무기 파워 서열 (필드 상대 평균 승률):");
        foreach (var (w, avg) in rows.OrderByDescending(r => r.avg))
            Console.WriteLine($"   {w,-5} {avg,5:F1}{(avg > 60 ? "  ◀ 과강" : avg < 40 ? "  ◀ 과약" : "")}");
        float spread = rows.Max(r => r.avg) - rows.Min(r => r.avg);
        Console.WriteLine($"\n파워 스프레드(최강-최약): {spread:F1}%p  (목표: ≤ 20%p)");
    }

    private static string Short(string wpn) => wpn switch
    {
        "WPN_SWORD" => "검", "WPN_SPEAR" => "창", "WPN_AXE" => "도끼", "WPN_GREATSWORD" => "대검",
        "WPN_DUALBLADES" => "쌍검", "WPN_HAMMER" => "망치", "WPN_WHIP" => "채찍", "WPN_SHIELD" => "방패",
        _ => wpn,
    };

    // 무기 → 시그니처 전술 (간격 측정용)
    private static string SigTac(string w) => w switch
    {
        "WPN_SPEAR" => "TAC_COUNTER", "WPN_WHIP" => "TAC_ZONER", "WPN_DUALBLADES" => "TAC_BRAWLER",
        "WPN_SHIELD" => "TAC_DEFENDER", "WPN_AXE" => "TAC_BRAWLER", _ => "TAC_PRESSURE",
    };

    /// <summary>간격 측정: 전 무기(시그니처 빌드) 페어별 평균/최소/최대 gap + 무기별 평균. 사거리/거리 스케일 영향 관찰.</summary>
    public static void SpacingProbe(int games)
    {
        Console.WriteLine($"=== 간격 측정 (전 무기 페어, 매치업당 {games}경기, 프레임 평균) ===\n");
        var perW = new Dictionary<string, (double sum, long n)>();
        double allSum = 0; long allN = 0; double allMin = 1e9, allMax = 0;
        foreach (var wa in AllWeapons)
        {
            double wsum = 0; long wn = 0;
            foreach (var wb in AllWeapons)
            {
                var a = new FighterDef(Short(wa), FighterStats.Baseline, wa, SigTac(wa), "PER_CALM");
                var b = new FighterDef(Short(wb), FighterStats.Baseline, wb, SigTac(wb), "PER_CALM");
                for (ulong s = 1; s <= (ulong)games; s++)
                {
                    var frames = new List<ReplayFrame>();
                    new MatchSim().Run(a, b, s, null, frames);
                    foreach (var f in frames)
                    {
                        double g = Math.Sqrt((f.Bx - f.Ax) * (f.Bx - f.Ax) + (f.By - f.Ay) * (f.By - f.Ay));
                        wsum += g; wn++; allSum += g; allN++;
                        if (g < allMin) allMin = g; if (g > allMax) allMax = g;
                    }
                }
            }
            perW[wa] = (wsum, wn);
        }
        Console.WriteLine("무기      평균 gap(자기 모든 매치업)");
        foreach (var w in AllWeapons)
            Console.WriteLine($"  {Short(w),-5} {perW[w].sum / perW[w].n,5:F2}m");
        Console.WriteLine($"\n전체 평균 gap: {allSum / allN:F2}m  (최소 {allMin:F2} / 최대 {allMax:F2})");
    }

    /// <summary>패링 성공률 프로브: 방패(방어형)가 각 무기(압박)를 상대로 패링 vs 칩블록 비율·기절·승률.</summary>
    public static void ParryProbe(int games)
    {
        Console.WriteLine($"=== 패링 성공률 프로브 (방패+방어형 vs 각 무기·압박, 매치업당 {games}경기) ===");
        Console.WriteLine($"  ParryWindow = {WeaponTable.Shield.ParryWindowSec * 1000f:F0}ms · ParryChance = {BalanceConstants.Default.ParryChance:P0}\n");
        Console.WriteLine("상대무기  패링   칩블록  패링률   기절유발  방패승률");
        int totP = 0, totB = 0, totS = 0;
        foreach (var w in AllWeapons)
        {
            var a = new FighterDef("방패", FighterStats.Baseline, "WPN_SHIELD", "TAC_DEFENDER", "PER_CALM");
            var b = new FighterDef(Short(w), FighterStats.Baseline, w, "TAC_PRESSURE", "PER_CALM");
            int parries = 0, blocks = 0, stuns = 0, shieldWins = 0;
            for (ulong s = 1; s <= (ulong)games; s++)
            {
                var ev = new List<SimEvent>();
                var r = new MatchSim().Run(a, b, s, ev);
                if (r.Winner == 0) shieldWins++;
                foreach (var e in ev)
                {
                    if (e is Parried p && p.Defender == 0) { parries++; if (p.StunStacks == 0) stuns++; }
                    else if (e is HitLanded h && h.Defender == 0 && h.IsGuarded) blocks++;
                }
            }
            int tot = parries + blocks;
            totP += parries; totB += blocks; totS += stuns;
            Console.WriteLine($"  {Short(w),-5} {parries,6} {blocks,7} {(tot > 0 ? 100f * parries / tot : 0f),6:F1}% {stuns,8} {100f * shieldWins / games,7:F1}%");
        }
        int gt = totP + totB;
        Console.WriteLine($"\n  전체 패링률: {(gt > 0 ? 100f * totP / gt : 0f):F1}%  (패링 {totP} / 블록 {totB}), 기절 {totS}회");
    }
}
