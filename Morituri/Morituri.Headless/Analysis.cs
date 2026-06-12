using Morituri.Sim.Data;
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
        ("TAC_DEFENDER", "방어",   "WPN_SWORDSHIELD"), // 방패검 방어 특화
    };

    private static readonly string[] AllWeapons =
    {
        "WPN_SWORD", "WPN_SPEAR", "WPN_AXE", "WPN_GREATSWORD",
        "WPN_DUALBLADES", "WPN_HAMMER", "WPN_WHIP", "WPN_SWORDSHIELD",
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

    private static string Short(string wpn) => wpn switch
    {
        "WPN_SWORD" => "검", "WPN_SPEAR" => "창", "WPN_AXE" => "도끼", "WPN_GREATSWORD" => "대검",
        "WPN_DUALBLADES" => "쌍검", "WPN_HAMMER" => "망치", "WPN_WHIP" => "채찍", "WPN_SWORDSHIELD" => "방패",
        _ => wpn,
    };
}
