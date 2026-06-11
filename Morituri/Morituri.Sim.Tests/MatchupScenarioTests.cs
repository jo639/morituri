using Morituri.Sim.Combat;
using Morituri.Sim.Data;

namespace Morituri.Sim.Tests;

/// <summary>
/// 문서[4] 11장 상성 검증 시나리오: 버서커(도끼) vs 전술가(창).
/// AI가 붙기 전, "수식만으로 이 상성이 성립할 조건이 갖춰졌는가"를 검증한다.
/// 여기가 깨지면 M3 배치 테스트 전에 수치를 다시 봐야 한다.
/// </summary>
[TestFixture]
public class MatchupScenarioTests
{
    private static readonly BalanceConstants C = BalanceConstants.Default;
    private static readonly FighterStats Avg = FighterStats.Baseline;

    [Test]
    public void Scenario_AxeWhiff_GuaranteesSpearCounterWindow()
    {
        // 문서[4] 11장 2번: 도끼 후딜 0.85s ≫ 창 인지지연 + 찌르기 모션 → 헛스윙마다 카운터 확정
        float axeRecovery = WeaponTable.Axe.RecoverySec; // 0.85
        float spearPerception = CombatMath.PerceptionDelay(Avg); // RCT 70 → 0.16
        // 창 찌르기 선딜을 0.4초로 가정 (T02 모션 데이터 확정 전 임시값)
        float spearThrustWindup = CombatMath.MotionTime(0.4f, WeaponTable.Spear, Avg, C);

        float counterArrival = spearPerception + spearThrustWindup;
        Assert.That(counterArrival, Is.LessThan(axeRecovery),
            $"창의 카운터 도달({counterArrival:F3}s)이 도끼 후딜({axeRecovery}s)보다 빨라야 상성이 성립");
    }

    [Test]
    public void Scenario_SpearNeeds2or3Hits_PerAxeHeavyHit()
    {
        // 문서[4] 11장 3번: "창의 2~3대 = 도끼의 1대"
        var clean = CombatMath.HitContext.Clean;
        float axeHeavy = CombatMath.FinalDamage(WeaponTable.Axe, C.MotionMultHeavy, Avg, Avg, clean, C);
        // 창의 주력은 카운터 약공 (CounterMult 1.35 포함이 실전 상황)
        var counter = clean with { IsCounter = true };
        float spearCounterLight = CombatMath.FinalDamage(WeaponTable.Spear, C.MotionMultLight, Avg, Avg, counter, C);

        float ratio = axeHeavy / spearCounterLight;
        Assert.That(ratio, Is.InRange(2.0f, 3.0f),
            $"도끼 강공 1대 = 창 카운터 {ratio:F2}대 — 문서 목표 2~3대 범위");
    }

    [Test]
    public void Scenario_AxeHeavy_BreaksSpearPoise_RequiresPoiseMaxAtMost45()
    {
        // ⚠ 스펙 빈틈 발견 (M1 산출물): 도끼 PoiseDmg 30 × 강공 1.5 = 45.
        // 문서[4] 11장 3번 "도끼 적중 → 창 Poise 파괴" 시나리오는
        // 창 사용자 PoiseMax ≤ 45일 때만 성립한다. PoiseMax는 "무기/체급 의존"으로만
        // 정의되어 있고 수치 미정 → T06/T01에 PoiseMax 컬럼 확정 필요 (오픈 이슈 #1).
        var breaks = CombatMath.ApplyPoiseDamage(45f, 45f, WeaponTable.Axe, C.MotionMultHeavy, false, C);
        Assert.That(breaks.IsStagger, Is.True, "PoiseMax 45 이하면 시나리오 성립");

        var holds = CombatMath.ApplyPoiseDamage(60f, 60f, WeaponTable.Axe, C.MotionMultHeavy, false, C);
        Assert.That(holds.IsStagger, Is.False, "PoiseMax 60이면 시나리오 불성립 — 경량 무기 PoiseMax를 45 이하로 잡아야 함");
    }

    [Test]
    public void Scenario_SpearInsideAxeRange_LosesDamageRace()
    {
        // 문서[4] 8장: 도끼가 창 사거리 안쪽(0.7m)으로 파고들면 창은 ×0.6 패널티
        // → 거리 붕괴 = 창의 패배 조건이 수식으로 성립하는지
        var clean = CombatMath.HitContext.Clean;
        var inner = clean with { IsInnerRange = true };

        float spearInside = CombatMath.FinalDamage(WeaponTable.Spear, C.MotionMultLight, Avg, Avg, inner, C);
        float axeInside   = CombatMath.FinalDamage(WeaponTable.Axe,   C.MotionMultLight, Avg, Avg, clean, C);

        Assert.That(axeInside, Is.GreaterThan(spearInside * 2f),
            "근접전에서 도끼 효율이 창을 압도해야 '파고들기'가 유효 전략이 된다");
    }

    [Test]
    public void Scenario_TimeToKill_WithinTargetMatchLength()
    {
        // 문서[4] 12장 목표: 평균 경기 60~150초.
        // 거칠게: 평균 선수 HP 700, 도끼 강공 평타(~104.6) 기준 7대 = KO.
        // 도끼 강공 사이클(선딜0.5 가정+후딜0.85+접근)을 ~3초로 보면 명중률 50%에서 ~42초 — 하한 OK.
        // 이 테스트는 "한 방이 HP의 5~25% 사이"라는 완화된 불변식만 고정한다.
        var clean = CombatMath.HitContext.Clean;
        foreach (var w in WeaponTable.All)
        {
            float heavy = CombatMath.FinalDamage(w, C.MotionMultHeavy, Avg, Avg, clean, C);
            float pctOfHp = heavy / Avg.HpMax;
            Assert.That(pctOfHp, Is.InRange(0.05f, 0.25f),
                $"{w.Id} 강공이 평균 HP의 {pctOfHp:P1} — 5% 미만이면 경기가 늘어지고 25% 초과면 4방 게임");
        }
    }

    [Test]
    public void Scenario_EveryWeapon_DealsMeaningfulDamageVsMaxDef()
    {
        // 승산곡선 채택 이유 검증: DEF 150 탱커에게도 쌍검이 0데미지가 되지 않는다
        var tank = Avg with { Def = 150 };
        var clean = CombatMath.HitContext.Clean;
        float dmg = CombatMath.FinalDamage(WeaponTable.DualBlades, C.MotionMultLight, Avg, tank, clean, C);
        Assert.That(dmg, Is.GreaterThan(Avg.HpMax * 0.01f), "최약 조합도 HP 1% 이상은 깎아야 상성이 살아있음");
    }
}
