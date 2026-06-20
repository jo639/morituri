using Morituri.Sim.Data;
using Morituri.Sim.Events;
using Morituri.Sim.Match;

namespace Morituri.Headless;

/// <summary>
/// 경기 한 판을 SimEvent 스트림으로 받아 텍스트 중계로 출력한다.
/// 그래픽 프레젠테이션(M4) 이전에, 전술×성격이 실제로 어떻게 싸우는지 눈으로 검증하는 진단 도구.
/// </summary>
internal static class Replay
{
    public static void Run(string matchup, ulong seed, bool verbose = false)
    {
        var (a, b) = Pick(matchup);
        var events = new List<SimEvent>();
        var result = new MatchSim().Run(a, b, seed, events);

        string[] name = { a.Name, b.Name };
        float[] hpMax = { a.Stats.HpMax, b.Stats.HpMax };
        float[] hp = { hpMax[0], hpMax[1] };

        Console.WriteLine($"=== 리플레이: {a.Name} vs {b.Name} (시드 {seed}) ===");
        Console.WriteLine($"    {a.Name}: {a.WeaponId} / {a.TacticsId} / {a.PersonalityId}");
        Console.WriteLine($"    {b.Name}: {b.WeaponId} / {b.TacticsId} / {b.PersonalityId}\n");

        foreach (var e in events)
        {
            switch (e)
            {
                case StateChanged ev when verbose:
                    Console.WriteLine($"{T(ev.Time)}     · {name[ev.FighterId]} 상태 {ev.From}→{ev.To}");
                    break;

                case AttackSwung ev:
                    Console.WriteLine($"{T(ev.Time)} {name[ev.FighterId]} ▶ {(ev.IsFeint ? "페인트" : "공격")} 휘두름 ({ev.MotionId})");
                    break;

                case HitLanded ev:
                    hp[ev.Defender] = MathF.Max(0f, hp[ev.Defender] - ev.Damage);
                    string tag = ev.IsArmored ? "🪨 몸으로받음" : ev.IsGuarded ? "🛡 가드" : ev.IsCounter ? "⚡ 카운터" : ev.IsCrit ? "💢 치명타" : "💥 명중";
                    Console.WriteLine($"{T(ev.Time)}   {tag}  {name[ev.Attacker]} → {name[ev.Defender]}  -{ev.Damage:F0}  " +
                                      $"[{name[ev.Defender]} {Bar(hp[ev.Defender], hpMax[ev.Defender])} {hp[ev.Defender]:F0}/{hpMax[ev.Defender]:F0}]");
                    break;

                case PoiseBroken ev:
                    Console.WriteLine($"{T(ev.Time)}   〽 {name[ev.FighterId]} 자세 무너짐 (Stagger)");
                    break;

                case GuardBroken ev:
                    Console.WriteLine($"{T(ev.Time)}   🔨 {name[ev.FighterId]} 가드 파괴!");
                    break;

                case KnockedDown ev:
                    Console.WriteLine($"{T(ev.Time)}   ⬇ {name[ev.FighterId]} 넘어짐 (Down)");
                    break;

                case StaminaExhausted ev:
                    Console.WriteLine($"{T(ev.Time)}   😮‍💨 {name[ev.FighterId]} 스태미나 고갈 (Exhausted)");
                    break;

                case Decision ev:
                    Console.WriteLine($"{T(ev.Time)} 🧠 {name[ev.FighterId]} 판단: {ev.ReasonTag} ({ev.SourceLayer}층, {ev.Duration:F1}s)");
                    break;

                case MatchEnded ev:
                    string who = ev.Winner == -1 ? "무승부" : $"{name[ev.Winner]} 승";
                    Console.WriteLine($"\n{T(ev.Time)} 🏁 경기 종료 — {who} ({ev.Reason}, 판정 {ev.ScoreA:F0} : {ev.ScoreB:F0})");
                    break;
            }
        }

        Console.WriteLine($"\n총 {events.Count}개 이벤트 / {result.DurationSec:F1}초");
        Console.WriteLine($"   {a.Name}: 시도 {result.StatsA.AttackAttempts}회 (헛스윙 {result.StatsA.Whiffs}) / 클린히트 {result.StatsA.CleanHits} / 누적딜 {result.StatsA.DamageDealt:F0}");
        Console.WriteLine($"   {b.Name}: 시도 {result.StatsB.AttackAttempts}회 (헛스윙 {result.StatsB.Whiffs}) / 클린히트 {result.StatsB.CleanHits} / 누적딜 {result.StatsB.DamageDealt:F0}");
    }

    internal static (FighterDef, FighterDef) Pick(string m)
    {
        // "t:COUNTER:PRESSURE" = 전술 매트릭스 진단용 (검+냉철함 고정, 매트릭스 배치와 동일 조건)
        if (m.StartsWith("t:"))
        {
            var p = m.Split(':');
            return (new FighterDef(p[1], FighterStats.Baseline, "WPN_SWORD", "TAC_" + p[1], "PER_CALM"),
                    new FighterDef(p[2], FighterStats.Baseline, "WPN_SWORD", "TAC_" + p[2], "PER_CALM"));
        }
        // "w:AXE:SWORD" = 무기 진단용 (균형형+냉철함 고정)
        if (m.StartsWith("w:"))
        {
            var p = m.Split(':');
            return (new FighterDef(p[1], FighterStats.Baseline, "WPN_" + p[1], "TAC_BALANCED", "PER_CALM"),
                    new FighterDef(p[2], FighterStats.Baseline, "WPN_" + p[2], "TAC_BALANCED", "PER_CALM"));
        }
        // "s:WHIP:DUALBLADES" = 시그니처 빌드 진단용 (각 무기가 제 시그니처 전술 — sigmatrix 셀과 동일 조건).
        // disc 재설계(doc[9]) 레버 검증: 카이터 vs 러셔 같은 실제 매치업을 replay/viewer로 재현.
        if (m.StartsWith("s:"))
        {
            var p = m.Split(':');
            return (new FighterDef(p[1], FighterStats.Baseline, "WPN_" + p[1], SigTactic(p[1]), "PER_CALM"),
                    new FighterDef(p[2], FighterStats.Baseline, "WPN_" + p[2], SigTactic(p[2]), "PER_CALM"));
        }
        // "b:SWORD/PRESSURE/ARROGANT:AXE/BALANCED/RECKLESS" = 무기/전술/성격 직접 지정.
        // 각 빌드는 무기/전술/성격, 빈 필드는 기본값(SWORD/BALANCED/CALM). B 빌드 생략 시 A의 거울.
        // 예: "b:WHIP/ZONER/SHOWMAN:AXE/BRAWLER/CRUEL", "b:/PRESSURE/CRUEL"(검·압박·잔혹 vs 거울)
        if (m.StartsWith("b:"))
        {
            var p = m.Split(':');
            var a = BuildFighter(p.Length > 1 ? p[1] : "", 0);
            var b = BuildFighter(p.Length > 2 ? p[2] : (p.Length > 1 ? p[1] : ""), 1);
            return (a, b);
        }
        return PickNamed(m);
    }

    // "무기/전술/성격" 한 빌드 → FighterDef. 빈 필드는 기본값으로 채우고 ID 프리픽스를 자동 부착한다.
    private static FighterDef BuildFighter(string spec, int idx)
    {
        var f = spec.Split('/');
        string wpn = Field(f, 0, "SWORD");
        string tac = Field(f, 1, "BALANCED");
        string per = Field(f, 2, "CALM");
        string name = $"P{idx + 1}·{per}·{tac}·{wpn}";
        return new FighterDef(name, FighterStats.Baseline, "WPN_" + wpn, "TAC_" + tac, "PER_" + per);
    }

    private static string Field(string[] f, int i, string def)
        => i < f.Length && f[i].Length > 0 ? f[i].Trim().ToUpperInvariant() : def;

    // 무기 → 시그니처 전술 (Analysis.SignatureMatrix의 5종 + 중량 3종). sigmatrix 셀 재현용.
    private static string SigTactic(string wpn) => wpn switch
    {
        "SWORD" => "TAC_PRESSURE", "SPEAR" => "TAC_COUNTER", "WHIP" => "TAC_ZONER",
        "DUALBLADES" => "TAC_BRAWLER", "SWORDSHIELD" => "TAC_DEFENDER",
        "AXE" => "TAC_BRAWLER", "GREATSWORD" => "TAC_PRESSURE", "HAMMER" => "TAC_PRESSURE",
        _ => "TAC_BALANCED",
    };

    private static (FighterDef, FighterDef) PickNamed(string m) => m switch
    {
        "mirror" => (new FighterDef("A", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM"),
                     new FighterDef("B", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM")),
        "cruel" => (new FighterDef("학살자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL"),
                    new FighterDef("허당", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_COWARD")),
        "arrogant" => (new FighterDef("챔피언", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_ARROGANT"),
                       new FighterDef("도전자", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM")),
        "cruelanvil" => (new FighterDef("학살자", FighterStats.Baseline, "WPN_SWORD", "TAC_PRESSURE", "PER_CRUEL"),
                         new FighterDef("기준", FighterStats.Baseline, "WPN_SWORD", "TAC_BALANCED", "PER_CALM")),
        _ => (FighterDef.Berserker, FighterDef.Tactician),
    };

    private static string T(float t) => $"[{(int)t / 60:D2}:{t % 60:00.0}]";

    private static string Bar(float v, float max)
    {
        int filled = (int)MathF.Round(10f * MathF.Max(0f, v) / max);
        return new string('█', filled) + new string('░', 10 - filled);
    }
}
