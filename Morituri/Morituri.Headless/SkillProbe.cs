using Morituri.Sim.Data;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 스킬 수치 약식 검수 — 같은 시드·같은 빌드로 <b>스킬 하나만 얹어</b> 대조군과 비교한다.
/// 목적은 정밀 밸런싱이 아니라 <b>이상치 탐지</b>: 승률 델타가 과하면(±15%p) 수치가 세다는 신호.
/// [7]§8 파워스윙(자힐·즉사·전능력 폭발)은 여기서 먼저 걸러내고, 본 튜닝은 sigmatrix로.
/// 사용: dotnet run -- skillprobe [대진당 경기수] [스킬필터]
///
/// <b>왜 대진 패널인가</b> — 단일 대진 승률 Δ는 세 가지로 무너진다(전부 실측 확인):
///  1) 포화: 대조 승률이 0/100%에 붙으면 Δ가 압축된다. 공간 지배는 68% 대진에서 +31%p로 보였지만
///     2% 대진에선 +97%p였다(승률 2%를 100%로 뒤집는 스킬이 '★과함'으로 찍혔다).
///  2) 거울 부풀림: 같은 무기끼리는 승률이 50%로 예쁘게 나오지만 양쪽이 동일해 작은 우위가 승부를
///     결정한다. 대지 강타는 거울에서 +33.8%p(!!위험), 비거울에서 +3.8%p(정상)였다 — 수치는 멀쩡했다.
///  3) 상대 하나에 판정이 좌우: 광전사의 도끼는 도끼 상대 −29%p, 대검 상대 −39%p.
/// → 상대를 하나 고르지 않고 <b>비거울 패널 전체</b>와 붙여 평균으로 판정한다. 대진 고르기 문제 자체가 사라진다.
///
/// <b>왜 HP 마진을 같이 보나</b> — 승률은 0/100%에서 막히지만 HP 마진(내 잔여HP% − 상대 잔여HP%)은
/// 계속 움직인다. 이미 이기는 대진에서 '얼마나 더 압도하는지'가 마진에만 남으므로 포화가 원천 차단된다.
/// </summary>
public static class SkillProbe
{
    // 스킬 소유자 빌드만 고정한다(액티브=게이트 무기, 패시브=게이트 성격). 상대는 아래 패널이 담당.
    private static readonly (string Skill, string W, string T, string P)[] Cases =
    {
        // ── 무기 액티브 ──
        ("SKL_COMBO",       "WPN_SWORD","TAC_HUNTER","PER_CALM"),
        ("SKL_GUARDSTANCE", "WPN_SWORD","TAC_DEFENDER","PER_WARY"),
        ("SKL_REACHPUSH",   "WPN_SPEAR","TAC_ZONER","PER_WARY"),
        ("SKL_ZONELOCK",    "WPN_SPEAR","TAC_ZONER","PER_WARY"),
        ("SKL_SUNDER",      "WPN_AXE","TAC_HUNTER","PER_CRUEL"),
        ("SKL_BERSERK",     "WPN_AXE","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_CHARGE",      "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_UNBROKEN",    "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_FLURRY",      "WPN_DUALBLADES","TAC_PRESSURE","PER_BOLD"),
        ("SKL_MIRAGE",      "WPN_DUALBLADES","TAC_HUNTER","PER_CALM"),
        ("SKL_SMASH",       "WPN_HAMMER","TAC_PRESSURE","PER_BOLD"),
        ("SKL_EXECUTE",     "WPN_HAMMER","TAC_PRESSURE","PER_CRUEL"),
        ("SKL_LASH",        "WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("SKL_ENTANGLE",    "WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("SKL_CARRY",       "WPN_SHIELD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_SHIELDBASH",  "WPN_SHIELD","TAC_PRESSURE","PER_BOLD"),
        // ── 성격 패시브 ──
        ("SKL_COMPOSE",   "WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("SKL_READ",      "WPN_SWORD","TAC_COUNTER","PER_CALM"),
        ("SKL_FERVOR",    "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_LASTSTAND", "WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("SKL_LEISURE",   "WPN_SWORD","TAC_BALANCED","PER_ARROGANT"),
        ("SKL_IMPERIAL",  "WPN_SWORD","TAC_PRESSURE","PER_ARROGANT"),
        ("SKL_FAIRFIGHT", "WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("SKL_CHIVALRY",  "WPN_SWORD","TAC_BALANCED","PER_HONORABLE"),
        ("SKL_SURVIVE",   "WPN_SWORD","TAC_EVADER","PER_COWARD"),
        ("SKL_BACKSTAB",  "WPN_SWORD","TAC_HUNTER","PER_COWARD"),
        ("SKL_CROWD",     "WPN_SWORD","TAC_BALANCED","PER_SHOWMAN"),
        ("SKL_SHOWTIME",  "WPN_SWORD","TAC_PRESSURE","PER_SHOWMAN"),
        ("SKL_EXPLOIT",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST"),
        ("SKL_VULTURE",   "WPN_SWORD","TAC_HUNTER","PER_OPPORTUNIST"),
        ("SKL_BLOODLUST", "WPN_AXE","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_TERROR",    "WPN_AXE","TAC_BRAWLER","PER_CRUEL"),
        ("SKL_NERVE",     "WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("SKL_COMEBACK",  "WPN_SWORD","TAC_BALANCED","PER_BOLD"),
        ("SKL_GUARDED",   "WPN_SWORD","TAC_DEFENDER","PER_WARY"),
        ("SKL_FORESEE",   "WPN_SWORD","TAC_COUNTER","PER_WARY"),
    };

    // 상대 패널 — 8무기를 모두 덮는 대표 빌드. 소유자와 같은 무기(거울)는 매 스킬마다 제외한다.
    private static readonly (string W, string T, string P)[] Panel =
    {
        ("WPN_SWORD","TAC_BALANCED","PER_CALM"),
        ("WPN_SWORD","TAC_PRESSURE","PER_BOLD"),
        ("WPN_SWORD","TAC_DEFENDER","PER_HONORABLE"),
        ("WPN_AXE","TAC_BRAWLER","PER_RECKLESS"),
        ("WPN_GREATSWORD","TAC_PRESSURE","PER_BOLD"),
        ("WPN_HAMMER","TAC_PRESSURE","PER_CRUEL"),
        ("WPN_SPEAR","TAC_COUNTER","PER_CALM"),
        ("WPN_WHIP","TAC_ZONER","PER_WARY"),
        ("WPN_DUALBLADES","TAC_BRAWLER","PER_BOLD"),
        ("WPN_SHIELD","TAC_DEFENDER","PER_WARY"),
    };

    public static void Run(int games, string? only = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"=== 스킬 약식 검수: 비거울 패널 전체와 대진, 대진당 {games}경기 ===");
        Console.WriteLine("    대조군 = 같은 빌드·같은 시드에서 스킬만 제거.  판정은 패널 평균 승률Δ (|Δ|>15%p 과함★ / >25%p 위험!!)");
        Console.WriteLine("    HP마진Δ = (내 잔여HP% − 상대 잔여HP%)의 변화 — 승률이 천장/바닥이어도 이 값은 움직인다.\n");
        Console.WriteLine($"{"스킬",-16}{"발동/경기",10}{"승률Δ",9}{"HP마진Δ",10}{"대진별 최소~최대",18}  판정");
        Console.WriteLine(new string('─', 78));

        foreach (var c in Cases)
        {
            if (only != null && !c.Skill.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var sk = SkillTable.Exists(c.Skill) ? SkillTable.Get(c.Skill) : null;
            if (sk == null) { Console.WriteLine($"{c.Skill,-16}  (없는 스킬)"); continue; }
            string tag = sk.Active?.ReasonTag ?? sk.Passive?.ReasonTag ?? "";
            var pre = Prereq(c.Skill);

            float sumWr = 0f, sumMargin = 0f, minWr = float.MaxValue, maxWr = float.MinValue;
            int panelN = 0, procs = 0, matches = 0;

            foreach (var o in Panel)
            {
                if (o.W == c.W) continue;                       // 거울 제외
                int winBase = 0, winSkill = 0;
                float marBase = 0f, marSkill = 0f;
                for (int g = 0; g < games; g++)
                {
                    ulong seed = (ulong)(g * 7919 + 13);
                    var opp = new FighterDef("상대", FighterStats.Baseline, o.W, o.T, o.P);
                    var oppSk = OppEquip(c.Skill, o.W);
                    if (oppSk.Length > 0) opp = opp with { TraitIds = oppSk };

                    var baseF = new FighterDef("본인", FighterStats.Baseline, c.W, c.T, c.P);
                    if (pre.Length > 0) baseF = baseF with { TraitIds = pre };
                    var (wb, _, mb) = RunOne(baseF, opp, seed, "");
                    if (wb == 0) winBase++;
                    marBase += mb;

                    var withF = baseF with { TraitIds = pre.Append(c.Skill).ToArray() };
                    var (ws, n, ms) = RunOne(withF, opp, seed, tag);
                    if (ws == 0) winSkill++;
                    marSkill += ms;
                    procs += n; matches++;
                }
                float dWr = 100f * (winSkill - winBase) / games;
                sumWr += dWr; sumMargin += (marSkill - marBase) / games;
                if (dWr < minWr) minWr = dWr;
                if (dWr > maxWr) maxWr = dWr;
                panelN++;
            }
            if (panelN == 0) { Console.WriteLine($"{sk.Def.Name.Replace("(스킬)", ""),-16}  (패널 없음)"); continue; }

            float mWr = sumWr / panelN, mMar = sumMargin / panelN;
            string verdict = MathF.Abs(mWr) > 25f ? "!! 위험" : MathF.Abs(mWr) > 15f ? "★ 과함" : "정상";
            string spread = $"{minWr,+6:+0.0;-0.0} ~{maxWr,+6:+0.0;-0.0}";
            Console.WriteLine($"{sk.Def.Name.Replace("(스킬)", ""),-16}{procs / (float)matches,10:F1}{mWr,8:+0.0;-0.0}p{mMar,9:+0.0;-0.0}p{spread,18}  {verdict}");
        }
        Console.WriteLine($"\n※ 판정은 패널 {Panel.Length - 1}~{Panel.Length}종의 평균이다 — 상대를 하나 고르지 않으므로 대진 선택이 결과를 좌우하지 않는다.");
        Console.WriteLine("※ 최소~최대 폭이 크면 '특정 상성에서만 강한' 스킬이다. 평균이 정상이어도 폭이 40%p를 넘으면 따로 볼 것.");
        Console.WriteLine("※ 승률Δ가 작은데 HP마진Δ가 크면 이미 이기는 대진을 '더 크게' 이기는 것 — 포화에 가려진 힘이다.");
        Console.WriteLine("※ 발동 0회면 트리거가 안 열린 것(코스트·조건). 수치가 아니라 조건 문제.");
        Console.WriteLine($"※ 표본: 대진당 {games}경기 × 패널 = 실효 {games * (Panel.Length - 1)}경기. 경계 근처는 대진당 400경기로 재확인.");
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
    /// 상대에게 물릴 스킬 — '상대가 액티브를 쓴 직후'가 조건인 함정 간파는 상대가 무장하지 않으면
    /// 조건이 영원히 안 열린다. 패널 상대마다 무기가 다르므로 그 무기의 대표 액티브를 물린다.
    /// </summary>
    private static string[] OppEquip(string skill, string oppWeapon) => skill switch
    {
        "SKL_FORESEE" => SignatureActive(oppWeapon),
        _ => Array.Empty<string>(),
    };

    private static string[] SignatureActive(string w) => w switch
    {
        "WPN_SWORD"      => new[] { "SKL_COMBO" },
        "WPN_AXE"        => new[] { "SKL_SUNDER" },
        "WPN_GREATSWORD" => new[] { "SKL_CHARGE" },
        "WPN_HAMMER"     => new[] { "SKL_SMASH" },
        "WPN_SPEAR"      => new[] { "SKL_REACHPUSH" },
        "WPN_WHIP"       => new[] { "SKL_LASH" },
        "WPN_DUALBLADES" => new[] { "SKL_FLURRY" },
        "WPN_SHIELD"     => new[] { "SKL_SHIELDBASH" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// 한 경기 → (승자, 발동수, HP마진%p). 발동수는 <b>검사 대상 태그</b>로 센다 —
    /// FighterId로 세면 선행 스킬이 섞이고, 공포 군림처럼 피격자에게 붙는 이벤트를 0으로 오독한다.
    /// </summary>
    private static (int Winner, int Procs, float MarginPp) RunOne(FighterDef a, FighterDef b, ulong seed, string tag)
    {
        var events = tag.Length == 0 ? null : new List<Morituri.Sim.Events.SimEvent>();
        var res = new MatchSim().Run(a, b, seed, events, null);
        int procs = events == null ? 0 : events.Count(e => e is Morituri.Sim.Events.Decision d
            && (d.ReasonTag == "SKILL_" + tag || d.ReasonTag == "PASV_" + tag));
        float margin = (res.StatsA.HpRemainPct - res.StatsB.HpRemainPct) * 100f;
        return (res.Winner, procs, margin);
    }
}
