using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 액티브 스킬 <b>사용빈도</b> 계측 — "쿨이 돌았을 때 실제로 쓰는가"를 본다.
/// skillprobe가 위력(승률 Δ)을 보는 도구라면, 이쪽은 기회 활용률을 본다.
///
/// 계측 방식: Sim을 건드리지 않고 이벤트 로그만으로 쿨타임 타임라인을 복원한다.
///   준비시각₁ = 0, 준비시각ₖ = 발동ₖ₋₁ + Duration + Cooldown
///   유휴ₖ = 발동ₖ − 준비시각ₖ   (= 쓸 수 있었는데 안 쓴 시간)
///   활용률 = 실제 발동수 ÷ 이론 상한(경기시간 ÷ 한 주기)
/// 대진 편향을 줄이려고 상대는 8무기 전부를 돌린다.
/// 사용: dotnet run -- skillfreq [시드수]
/// </summary>
public static class SkillFreq
{
    // 액티브 소유자의 전술·성격 — 그 무기의 표준 운용(skillprobe의 대진 선정과 같은 기조)
    private static readonly Dictionary<string, (string T, string P)> OwnerStyle = new()
    {
        ["WPN_SWORD"]      = ("TAC_BALANCED", "PER_CALM"),
        ["WPN_SPEAR"]      = ("TAC_COUNTER",  "PER_WARY"),
        ["WPN_AXE"]        = ("TAC_BRAWLER",  "PER_CRUEL"),
        ["WPN_GREATSWORD"] = ("TAC_PRESSURE", "PER_BOLD"),
        ["WPN_DUALBLADES"] = ("TAC_BRAWLER",  "PER_BOLD"),
        ["WPN_HAMMER"]     = ("TAC_PRESSURE", "PER_BOLD"),
        ["WPN_WHIP"]       = ("TAC_ZONER",    "PER_WARY"),
        ["WPN_SHIELD"]     = ("TAC_DEFENDER", "PER_HONORABLE"),
    };

    private static readonly string[] OppWeapons =
    {
        "WPN_SWORD", "WPN_SPEAR", "WPN_AXE", "WPN_GREATSWORD",
        "WPN_DUALBLADES", "WPN_HAMMER", "WPN_WHIP", "WPN_SHIELD",
    };

    public static void Run(int seeds)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var actives = SkillTable.All.Where(s => s.Active != null && s.GateWeapon != null).ToArray();

        Console.WriteLine($"=== 액티브 스킬 사용빈도 — 스킬당 {OppWeapons.Length}상대 × {seeds}시드 = {OppWeapons.Length * seeds}경기 ===");
        Console.WriteLine("  활용률 = 실제 발동 ÷ 이론 상한(경기시간÷주기).  유휴 = 쿨이 돌았는데 안 쓰고 흘린 시간의 비율");
        Console.WriteLine("  판정: 활용률 60%+ 양호 / 30~60% 낮음(▽) / 30% 미만 심각(▼▼)\n");
        Console.WriteLine($"{"스킬",-16}{"무기",-6}{"주기",6}{"발동/경기",10}{"이론상한",9}{"활용률",8}{"유휴%",7}{"무발동",7}{"첫발동",7}  판정");
        Console.WriteLine(new string('─', 92));

        var rows = new List<(string Name, string Wpn, float Util, float Idle, float PerMatch, float Zero)>();

        foreach (var sk in actives)
        {
            var sp = sk.Active!;
            string wpn = sk.GateWeapon!;
            var (tac, per) = OwnerStyle[wpn];
            float cycle = sp.Duration + sp.CooldownSec;

            int matches = 0, fires = 0, zeroMatches = 0;
            float sumDur = 0f, sumTheo = 0f, sumIdle = 0f, sumReadyWindow = 0f, sumFirst = 0f;
            int firstCount = 0;

            foreach (var ow in OppWeapons)
                for (int g = 0; g < seeds; g++)
                {
                    ulong seed = (ulong)(g * 7919 + 13);
                    var me  = new FighterDef("본인", FighterStats.Baseline, wpn, tac, per) { TraitIds = new[] { sk.Def.Id } };
                    var opp = new FighterDef("상대", FighterStats.Baseline, ow, "TAC_BALANCED", "PER_CALM");

                    var events = new List<SimEvent>();
                    var res = new MatchSim().Run(me, opp, seed, events, null);

                    var times = events.OfType<Decision>()
                        .Where(d => d.FighterId == 0 && d.ReasonTag == "SKILL_" + sp.ReasonTag)
                        .Select(d => d.Time).OrderBy(t => t).ToList();

                    matches++; fires += times.Count;
                    sumDur += res.DurationSec;
                    sumTheo += MathF.Floor(res.DurationSec / cycle) + 1f;   // t=0에 1회 가능 + 이후 주기마다
                    if (times.Count == 0) zeroMatches++;
                    else { sumFirst += times[0]; firstCount++; }

                    // 쿨 타임라인 복원 — 준비된 뒤 흘려보낸 시간을 합산한다
                    float ready = 0f, idle = 0f, window = 0f;
                    foreach (float t in times)
                    {
                        if (t >= ready) { idle += t - ready; window += t - ready; }
                        ready = t + cycle;
                    }
                    if (res.DurationSec > ready) { idle += res.DurationSec - ready; window += res.DurationSec - ready; }
                    sumIdle += idle; sumReadyWindow += window;
                }

            float perMatch = fires / (float)matches;
            float theo     = sumTheo / matches;
            float util     = theo > 0f ? 100f * perMatch / theo : 0f;
            // 유휴% = 준비완료 상태로 보낸 시간 ÷ 전체 경기시간
            float idlePct  = sumDur > 0f ? 100f * sumIdle / sumDur : 0f;
            float zeroPct  = 100f * zeroMatches / matches;
            float firstAt  = firstCount > 0 ? sumFirst / firstCount : -1f;
            string verdict = util >= 60f ? "양호" : util >= 30f ? "▽ 낮음" : "▼▼ 심각";

            Console.WriteLine($"{sk.Def.Name.Replace("(스킬)", ""),-16}{Short(wpn),-6}{cycle,5:F0}s{perMatch,10:F2}{theo,9:F1}"
                            + $"{util,7:F1}%{idlePct,6:F0}%{zeroPct,6:F1}%{(firstAt < 0 ? "  —" : $"{firstAt,6:F1}s"),7}  {verdict}");
            rows.Add((sk.Def.Name.Replace("(스킬)", ""), Short(wpn), util, idlePct, perMatch, zeroPct));
        }

        Console.WriteLine(new string('─', 92));
        Console.WriteLine($"평균 활용률 {rows.Average(r => r.Util),5:F1}%   평균 유휴 {rows.Average(r => r.Idle),5:F1}%   "
                        + $"평균 발동/경기 {rows.Average(r => r.PerMatch),4:F2}   무발동 경기 {rows.Average(r => r.Zero),4:F1}%");
        Console.WriteLine("\n※ 이론 상한은 '조건을 무시하고 쿨만 돌면 쓴다'는 가정 — 트리거가 좁은 스킬은 낮게 나오는 게 정상이다.");
        Console.WriteLine("※ 유휴%가 높은데 활용률이 낮으면 = 쓸 수 있는 시간이 길었는데 안 쓴 것(확률 롤·조건이 원인).");
    }

    private static string Short(string wpnId) => wpnId switch
    {
        "WPN_SWORD" => "검", "WPN_SPEAR" => "창", "WPN_AXE" => "도끼", "WPN_GREATSWORD" => "대검",
        "WPN_DUALBLADES" => "쌍검", "WPN_HAMMER" => "망치", "WPN_WHIP" => "채찍", "WPN_SHIELD" => "방패",
        _ => wpnId,
    };
}
