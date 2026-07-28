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
        ("SKL_COMBO",       "WPN_SWORD","TAC_HUNTER","PER_CALM",        "WPN_SHIELD","TAC_DEFENDER","PER_WARY"),
        ("SKL_GUARDSTANCE", "WPN_SWORD","TAC_DEFENDER","PER_WARY",      "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_REACHPUSH",   "WPN_SPEAR","TAC_ZONER","PER_WARY",         "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_ZONELOCK",    "WPN_SPEAR","TAC_ZONER","PER_WARY",         "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_SUNDER",      "WPN_AXE","TAC_HUNTER","PER_CRUEL",         "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("SKL_BERSERK",     "WPN_AXE","TAC_BRAWLER","PER_CRUEL",        "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_CHARGE",      "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD", "WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("SKL_UNBROKEN",    "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD", "WPN_HAMMER","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_FLURRY",      "WPN_DUALBLADES","TAC_PRESSURE","PER_BOLD", "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("SKL_MIRAGE",      "WPN_DUALBLADES","TAC_HUNTER","PER_CALM",   "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_SMASH",       "WPN_HAMMER","TAC_PRESSURE","PER_BOLD",     "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("SKL_EXECUTE",     "WPN_HAMMER","TAC_PRESSURE","PER_CRUEL",    "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_LASH",        "WPN_WHIP","TAC_ZONER","PER_WARY",          "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_ENTANGLE",    "WPN_WHIP","TAC_ZONER","PER_WARY",          "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_CARRY",       "WPN_SHIELD","TAC_PRESSURE","PER_BOLD",     "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("SKL_SHIELDBASH",  "WPN_SHIELD","TAC_PRESSURE","PER_BOLD",     "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        // ── 성격 패시브 ──
        ("SKL_COMPOSE",   "WPN_SWORD","TAC_BALANCED","PER_CALM",        "WPN_SWORD","TAC_PRESSURE","PER_ARROGANT"),
        ("SKL_READ",      "WPN_SWORD","TAC_COUNTER","PER_CALM",         "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_FERVOR",    "WPN_AXE","TAC_BRAWLER","PER_RECKLESS",       "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_LASTSTAND", "WPN_AXE","TAC_BRAWLER","PER_RECKLESS",       "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_LEISURE",   "WPN_SWORD","TAC_BALANCED","PER_ARROGANT",    "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_IMPERIAL",  "WPN_SWORD","TAC_PRESSURE","PER_ARROGANT",    "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_FAIRFIGHT", "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE",   "WPN_AXE","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_CHIVALRY",  "WPN_SWORD","TAC_BALANCED","PER_HONORABLE",   "WPN_AXE","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_SURVIVE",   "WPN_SWORD","TAC_EVADER","PER_COWARD",        "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_BACKSTAB",  "WPN_SWORD","TAC_HUNTER","PER_COWARD",        "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_CROWD",     "WPN_SWORD","TAC_BALANCED","PER_SHOWMAN",     "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_SHOWTIME",  "WPN_SWORD","TAC_PRESSURE","PER_SHOWMAN",     "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_EXPLOIT",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST",   "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_VULTURE",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST",   "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_BLOODLUST", "WPN_AXE","TAC_BRAWLER","PER_CRUEL",          "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_TERROR",    "WPN_AXE","TAC_BRAWLER","PER_CRUEL",          "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        // 배짱은 '강공 뒤 후딜' 스킬 — 검/압박은 강공을 한 번도 안 쓴다(71스윙 전부 약공). 중량 무기로 대진 교정.
        ("SKL_NERVE",     "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD",   "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_COMEBACK",  "WPN_SWORD","TAC_BALANCED","PER_BOLD",        "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_GUARDED",   "WPN_SWORD","TAC_DEFENDER","PER_WARY",        "WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_FORESEE",   "WPN_SWORD","TAC_COUNTER","PER_WARY",         "WPN_SWORD","TAC_BALANCED","PER_CALM"),
    };

    public static void Run(int games)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"=== 스킬 약식 검수: 케이스당 {games}경기 (대조군 = 같은 빌드·같은 시드, 스킬만 제거) ===");
        Console.WriteLine("    승률Δ = 스킬 장착 승률 − 대조군 승률.  |Δ|>15%p면 과함(★), >25%p면 위험(!!)\n");
        Console.WriteLine($"{"스킬",-16}{"발동/경기",10}{"대조 승률",10}{"장착 승률",10}{"Δ",8}  판정");
        Console.WriteLine(new string('─', 66));

        foreach (var c in Cases)
        {
            var sk = SkillTable.Exists(c.Skill) ? SkillTable.Get(c.Skill) : null;
            if (sk == null) { Console.WriteLine($"{c.Skill,-16}  (없는 스킬)"); continue; }

            int winBase = 0, winSkill = 0, procs = 0;
            for (int g = 0; g < games; g++)
            {
                ulong seed = (ulong)(g * 7919 + 13);
                var opp = new FighterDef("상대", FighterStats.Baseline, c.OW, c.OT, c.OP);
                // 대조군 — 선행 스킬이 있으면 그것만 장착해서 Δ가 '이 스킬의 순수 기여'가 되게 한다
                var pre = Prereq(c.Skill);
                var baseF = new FighterDef("본인", FighterStats.Baseline, c.W, c.T, c.P);
                if (pre.Length > 0) baseF = baseF with { TraitIds = pre };
                if (Winner(baseF, opp, seed) == 0) winBase++;
                // 스킬 장착
                var withF = baseF with { TraitIds = pre.Append(c.Skill).ToArray() };
                var (w, n) = WinnerAndProcs(withF, opp, seed);
                if (w == 0) winSkill++;
                procs += n;
            }
            float bp = 100f * winBase / games, sp = 100f * winSkill / games, d = sp - bp;
            string verdict = MathF.Abs(d) > 25f ? "!! 위험" : MathF.Abs(d) > 15f ? "★ 과함" : "정상";
            Console.WriteLine($"{sk.Def.Name.Replace("(스킬)", ""),-16}{procs / (float)games,10:F1}{bp,9:F0}%{sp,9:F0}%{d,7:+0.0;-0.0}p  {verdict}");
        }
        Console.WriteLine("\n※ 대진은 그 스킬이 의미를 갖는 상성으로 고정 — 절대 승률이 아니라 Δ만 본다.");
        Console.WriteLine("※ 발동 0회면 트리거가 이 대진에서 안 열린 것(코스트·조건). 수치가 아니라 조건 문제.");
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

    private static int Winner(FighterDef a, FighterDef b, ulong seed)
    {
        var res = new MatchSim().Run(a, b, seed, null, null);
        return res.Winner;
    }

    private static (int Winner, int Procs) WinnerAndProcs(FighterDef a, FighterDef b, ulong seed)
    {
        var events = new List<Morituri.Sim.Events.SimEvent>();
        var res = new MatchSim().Run(a, b, seed, events, null);
        int procs = events.Count(e => e is Morituri.Sim.Events.Decision d
            && d.FighterId == 0 && (d.ReasonTag.StartsWith("SKILL_") || d.ReasonTag.StartsWith("PASV_")));
        return (res.Winner, procs);
    }
}
