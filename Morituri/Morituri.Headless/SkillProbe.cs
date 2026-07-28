using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 스킬 수치 약식 검수 — 같은 시드·같은 빌드로 <b>스킬 하나만 얹어</b> 대조군과 비교한다.
/// 목적은 정밀 밸런싱이 아니라 <b>이상치 탐지</b>: 승률 델타가 과하면(±15%p) 수치가 세다는 신호.
/// [7]§8 파워스윙(자힐·즉사·전능력 폭발)은 여기서 먼저 걸러내고, 본 튜닝은 sigmatrix로.
/// 사용: dotnet run -- skillprobe [경기수]
/// </summary>
public static class SkillProbe
{
    // 각 스킬을 시험할 대진: (스킬 소유자 무기·전술·성격, 상대 무기·전술·성격)
    // 액티브는 게이트 무기로, 패시브는 게이트 성격으로 고정. 상대는 그 스킬이 의미를 갖는 상성.
    private static readonly (string Skill, string W, string T, string P, string OW, string OT, string OP)[] Cases =
    {
        // ── 무기 액티브 ──
        ("SKL_COMBO",       "WPN_SWORD","TAC_HUNTER","PER_CALM",        "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_GUARDSTANCE", "WPN_SWORD","TAC_DEFENDER","PER_WARY",      "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_REACHPUSH",   "WPN_SPEAR","TAC_ZONER","PER_WARY",         "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        // 공간 지배는 승률 0~2% 대진도 91~100%로 뒤집는다 — 대조 37%인 이 대진만 천장/바닥에 안 눌려 조정이 가능하다.
        ("SKL_ZONELOCK",    "WPN_SPEAR","TAC_ZONER","PER_WARY",         "WPN_DUALBLADES","TAC_HUNTER","PER_CALM"),
        ("SKL_SUNDER",      "WPN_AXE","TAC_HUNTER","PER_CRUEL",         "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_BERSERK",     "WPN_AXE","TAC_BRAWLER","PER_CRUEL",        "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_CHARGE",      "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD", "WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_UNBROKEN",    "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD", "WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_FLURRY",      "WPN_DUALBLADES","TAC_PRESSURE","PER_BOLD", "WPN_SWORD","TAC_EVADER","PER_COWARD"),
        ("SKL_MIRAGE",      "WPN_DUALBLADES","TAC_HUNTER","PER_CALM",   "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_SMASH",       "WPN_HAMMER","TAC_PRESSURE","PER_BOLD",     "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_EXECUTE",     "WPN_HAMMER","TAC_PRESSURE","PER_CRUEL",    "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        // ⚠ 채찍/존형은 후보 12종 어디에도 승률 20~80% 상대가 없다(전부 압승 아니면 완패).
        //    부득이 거울 대진이라 Δ가 부풀려진다 — 이 두 줄의 판정은 그대로 믿지 말 것.
        ("SKL_LASH",        "WPN_WHIP","TAC_ZONER","PER_WARY",          "WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("SKL_ENTANGLE",    "WPN_WHIP","TAC_ZONER","PER_WARY",          "WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("SKL_CARRY",       "WPN_SHIELD","TAC_PRESSURE","PER_BOLD",     "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_SHIELDBASH",  "WPN_SHIELD","TAC_PRESSURE","PER_BOLD",     "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        // ── 성격 패시브 ──
        // 침착은 '분노·도발 해제'라 도발하는 상대(오만)가 아니면 의미가 없다 — 평범한 상대로 바꾸니 발동 0회였다.
        ("SKL_COMPOSE",   "WPN_SWORD","TAC_BALANCED","PER_CALM",        "WPN_SPEAR","TAC_COUNTER","PER_ARROGANT"),
        ("SKL_READ",      "WPN_SWORD","TAC_COUNTER","PER_CALM",         "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_FERVOR",    "WPN_AXE","TAC_BRAWLER","PER_RECKLESS",       "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_LASTSTAND", "WPN_AXE","TAC_BRAWLER","PER_RECKLESS",       "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_LEISURE",   "WPN_SWORD","TAC_BALANCED","PER_ARROGANT",    "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_IMPERIAL",  "WPN_SWORD","TAC_PRESSURE","PER_ARROGANT",    "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_FAIRFIGHT", "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE",   "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        // 기사도는 'HP 15%p 열세'에서만 켜진다 — 도끼/난전 상대는 100% 이겨서 뒤질 일이 없었다(조건 미개방).
        ("SKL_CHIVALRY",  "WPN_SWORD","TAC_BALANCED","PER_HONORABLE",   "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_SURVIVE",   "WPN_SWORD","TAC_EVADER","PER_COWARD",        "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_BACKSTAB",  "WPN_SWORD","TAC_HUNTER","PER_COWARD",        "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_CROWD",     "WPN_SWORD","TAC_BALANCED","PER_SHOWMAN",     "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_SHOWTIME",  "WPN_SWORD","TAC_PRESSURE","PER_SHOWMAN",     "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_EXPLOIT",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST",   "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_VULTURE",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST",   "WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("SKL_BLOODLUST", "WPN_AXE","TAC_BRAWLER","PER_CRUEL",          "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_TERROR",    "WPN_AXE","TAC_BRAWLER","PER_CRUEL",          "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        // 배짱은 '강공 뒤 후딜' 스킬 — 검/압박은 강공을 한 번도 안 쓴다(71스윙 전부 약공). 중량 무기로 대진 교정.
        ("SKL_NERVE",     "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD",   "WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_COMEBACK",  "WPN_SWORD","TAC_BALANCED","PER_BOLD",        "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("SKL_GUARDED",   "WPN_SWORD","TAC_DEFENDER","PER_WARY",        "WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        // 함정 간파는 '상대 액티브 직후'가 조건 — 상대에게 연격을 물려야 조건이 열린다(OppEquip)
        ("SKL_FORESEE",   "WPN_SWORD","TAC_COUNTER","PER_WARY",         "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
    };

    public static void Run(int games, string? only = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"=== 스킬 약식 검수: 케이스당 {games}경기 (대조군 = 같은 빌드·같은 시드, 스킬만 제거) ===");
        Console.WriteLine("    승률Δ = 스킬 장착 승률 − 대조군 승률.  |Δ|>15%p면 과함(★), >25%p면 위험(!!)\n");
        Console.WriteLine($"{"스킬",-16}{"발동/경기",10}{"대조 승률",10}{"장착 승률",10}{"Δ",8}  판정");
        Console.WriteLine(new string('─', 66));

        foreach (var c in Cases)
        {
            if (only != null && !c.Skill.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var sk = SkillTable.Exists(c.Skill) ? SkillTable.Get(c.Skill) : null;
            if (sk == null) { Console.WriteLine($"{c.Skill,-16}  (없는 스킬)"); continue; }

            int winBase = 0, winSkill = 0, procs = 0;
            for (int g = 0; g < games; g++)
            {
                ulong seed = (ulong)(g * 7919 + 13);
                var opp = new FighterDef("상대", FighterStats.Baseline, c.OW, c.OT, c.OP);
                var oppSk = OppEquip(c.Skill);
                if (oppSk.Length > 0) opp = opp with { TraitIds = oppSk };
                // 대조군 — 선행 스킬이 있으면 그것만 장착해서 Δ가 '이 스킬의 순수 기여'가 되게 한다
                var pre = Prereq(c.Skill);
                var baseF = new FighterDef("본인", FighterStats.Baseline, c.W, c.T, c.P);
                if (pre.Length > 0) baseF = baseF with { TraitIds = pre };
                if (Winner(baseF, opp, seed) == 0) winBase++;
                // 스킬 장착
                var withF = baseF with { TraitIds = pre.Append(c.Skill).ToArray() };
                var (w, n) = WinnerAndProcs(withF, opp, seed, sk.Active?.ReasonTag ?? sk.Passive?.ReasonTag ?? "");
                if (w == 0) winSkill++;
                procs += n;
            }
            float bp = 100f * winBase / games, sp = 100f * winSkill / games, d = sp - bp;
            string verdict = MathF.Abs(d) > 25f ? "!! 위험" : MathF.Abs(d) > 15f ? "★ 과함" : "정상";
            Console.WriteLine($"{sk.Def.Name.Replace("(스킬)", ""),-16}{procs / (float)games,10:F1}{bp,9:F0}%{sp,9:F0}%{d,7:+0.0;-0.0}p  {verdict}");
        }
        Console.WriteLine("\n※ 대진은 그 스킬이 의미를 갖는 상성으로 고정 — 절대 승률이 아니라 Δ만 본다.");
        Console.WriteLine("※ 발동 0회면 트리거가 이 대진에서 안 열린 것(코스트·조건). 수치가 아니라 조건 문제.");
        Console.WriteLine($"※ 표본 주의: {games}경기 = 1경기당 {100f / games:F2}%p. ±15%p 판정선이 {(int)MathF.Ceiling(15f * games / 100f)}경기 차이라"
                        + " 경계 근처는 표본에 따라 뒤집힌다(60경기에서 ★였던 생존 본능이 800경기에선 +10p 정상)."
                        + " 판정 전 400경기 이상으로 재확인할 것 — 예: skillprobe 400 MIRAGE");
    }

    /// <summary>
    /// 선행 스킬 — 이게 없으면 조건 자체가 열리지 않아 측정이 불가능한 상위 스킬용.
    /// 쇼타임은 관중몰이가 쌓은 군중 스택을 태운다([7]§5 쇼맨 Ⅰ→Ⅱ 조합).
    /// </summary>
    private static string[] Prereq(string skill) => skill switch
    {
        "SKL_SHOWTIME" => new[] { "SKL_CROWD" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// 상대에게 물릴 스킬 — '상대가 액티브를 쓴 직후'가 조건인 함정 간파는
    /// 상대가 무장하지 않으면 조건이 영원히 안 열린다(측정 불가). 대조군 상대도 동일하게 맞춘다.
    /// </summary>
    private static string[] OppEquip(string skill) => skill switch
    {
        "SKL_FORESEE" => new[] { "SKL_SUNDER" }, // 분쇄 일격: 상대(도끼)가 자주 쓰는 액티브(경기당 5.6회)
        _ => Array.Empty<string>(),
    };


    /// <summary>
    /// 대진 건강검진 — 대조군 승률이 바닥/천장에 붙은 케이스는 스킬을 얹어도 Δ가 구조적으로 0이라
    /// '정상' 판정이 무의미하다(무측정). 그런 케이스에 대해 후보 상대를 훑어 50%에 가까운 대안을 제시한다.
    /// 사용: dotnet run -- matchfind [경기수]
    /// </summary>
    public static void FindMatchups(int games)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        (string W, string T, string P)[] pool =
        {
            ("WPN_SWORD","TAC_BALANCED","PER_CALM"),      ("WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
            ("WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"), ("WPN_SWORD","TAC_COUNTER","PER_CALM"),
            ("WPN_SWORD","TAC_EVADER","PER_COWARD"),      ("WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
            ("WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"), ("WPN_HAMMER","TAC_PRESSURE","PER_CRUEL"),
            ("WPN_SPEAR","TAC_COUNTER","PER_CALM"),       ("WPN_WHIP","TAC_ZONER","PER_WARY"),
            ("WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),  ("WPN_SHIELD","TAC_DEFENDER","PER_WARY"),
        };
        Console.WriteLine($"=== 대진 건강검진 (케이스당 {games}경기) — 대조 승률이 20% 미만/80% 초과면 무측정 ===");
        foreach (var c in Cases)
        {
            float cur = ControlWr(c, (c.OW, c.OT, c.OP), games);
            // 거울(같은 무기) 대진은 승률이 50%로 예쁘게 나오지만 Δ를 크게 부풀린다 —
            // 양쪽이 동일해 작은 우위가 승부를 결정하기 때문(대지 강타: 거울 +33.8%p vs 비거울 +3.8%p).
            bool mirror = c.OW == c.W;
            if (!mirror && cur >= 20f && cur <= 80f) continue;
            var sk = SkillTable.Exists(c.Skill) ? SkillTable.Get(c.Skill) : null;
            Console.WriteLine($"{sk?.Def.Name.Replace("(스킬)", "") ?? c.Skill,-12} 현재 {c.OW.Replace("WPN_","")}/{c.OT.Replace("TAC_","")} → 대조 {cur,3:F0}%  ({(mirror ? "거울" : "포화")})");
            var ranked = pool.Where(o => o.W != c.W)                    // 거울 제외
                             .Select(o => (o, wr: ControlWr(c, o, games)))
                             .Where(x => x.wr >= 20f && x.wr <= 80f)
                             .OrderBy(x => MathF.Abs(x.wr - 50f));
            foreach (var (o, wr) in ranked)
                Console.WriteLine($"    후보 {o.W.Replace("WPN_",""),-11}/{o.T.Replace("TAC_",""),-9}/{o.P.Replace("PER_",""),-11} → {wr,3:F0}%");
        }
    }

    private static float ControlWr((string Skill, string W, string T, string P, string OW, string OT, string OP) c,
                                   (string W, string T, string P) o, int games)
    {
        var pre = Prereq(c.Skill);
        int win = 0;
        for (int g = 0; g < games; g++)
        {
            ulong seed = (ulong)(g * 7919 + 13);
            var me = new FighterDef("본인", FighterStats.Baseline, c.W, c.T, c.P);
            if (pre.Length > 0) me = me with { TraitIds = pre };
            var opp = new FighterDef("상대", FighterStats.Baseline, o.W, o.T, o.P);
            var oppSk = OppEquip(c.Skill);
            if (oppSk.Length > 0) opp = opp with { TraitIds = oppSk };
            if (Winner(me, opp, seed) == 0) win++;
        }
        return 100f * win / games;
    }

    private static int Winner(FighterDef a, FighterDef b, ulong seed)
    {
        var res = new MatchSim().Run(a, b, seed, null, null);
        return res.Winner;
    }

    /// <summary>
    /// 발동 횟수는 <b>검사 대상 스킬의 태그</b>로 센다. FighterId로 세면 선행 스킬(관중몰이)이 섞이고,
    /// 공포 군림처럼 <b>피격자에게</b> 붙는 이벤트를 0으로 오독한다.
    /// </summary>
    private static (int Winner, int Procs) WinnerAndProcs(FighterDef a, FighterDef b, ulong seed, string tag)
    {
        var events = new List<Morituri.Sim.Events.SimEvent>();
        var res = new MatchSim().Run(a, b, seed, events, null);
        int procs = tag.Length == 0 ? 0 : events.Count(e => e is Morituri.Sim.Events.Decision d
            && (d.ReasonTag == "SKILL_" + tag || d.ReasonTag == "PASV_" + tag));
        return (res.Winner, procs);
    }
}
