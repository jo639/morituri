using Morituri.Sim.Combat;
using Morituri.Sim.Data;

namespace Morituri.Sim.Tests;

/// <summary>문서[4] 1~2장: 파생 스탯과 데미지 공식 검증. 기준 선수 = 전 스탯 70.</summary>
[TestFixture]
public class DamageFormulaTests
{
    private static readonly BalanceConstants C = BalanceConstants.Default;
    private static readonly FighterStats Avg = FighterStats.Baseline; // ATK/DEF/... 70, HP 700

    // ── 파생 스탯 ──

    [Test]
    public void StaminaMax_Hp700_Is95()
        => Assert.That(CombatMath.StaminaMax(Avg, C), Is.EqualTo(60f + 700f * 0.05f).Within(1e-4)); // 95

    [Test]
    public void GuardGaugeMax_Def70_Is68()
        => Assert.That(CombatMath.GuardGaugeMax(Avg, WeaponTable.Sword, C), Is.EqualTo(40f + 70f * 0.4f).Within(1e-4)); // 68

    [Test]
    public void GuardGaugeMax_SwordShield_Gets60PercentBonus()
    {
        float baseGauge = CombatMath.GuardGaugeMax(Avg, WeaponTable.Sword, C);
        float shieldGauge = CombatMath.GuardGaugeMax(Avg, WeaponTable.SwordShield, C);
        Assert.That(shieldGauge, Is.EqualTo(baseGauge * 1.6f).Within(1e-3));
    }

    [Test]
    public void MoveSpeed_Spd70_Is3_4mps()
        => Assert.That(CombatMath.MoveSpeedMps(Avg, C), Is.EqualTo(3.4f).Within(1e-4));

    [Test]
    public void MotionTime_Aspd70_Sword()
    {
        // 기본 1초 모션, 검(모션속도 1.0), ASPD 70 → 1 / (1.0 × (0.7 + 0.28)) = 1.0204...
        float t = CombatMath.MotionTime(1.0f, WeaponTable.Sword, Avg, C);
        Assert.That(t, Is.EqualTo(1f / 0.98f).Within(1e-4));
    }

    [Test]
    public void PerceptionDelay_FollowsDocExamples()
    {
        // 문서[3] 6.3: RCT 100 → 0.10초, RCT 30 → 0.24초
        Assert.That(CombatMath.PerceptionDelay(Avg with { Rct = 100 }), Is.EqualTo(0.10f).Within(1e-4));
        Assert.That(CombatMath.PerceptionDelay(Avg with { Rct = 30 }),  Is.EqualTo(0.24f).Within(1e-4));
        // clamp 검증: 상한 0.08, RCT 최소(1)는 0.298로 상한 0.30 미도달 (clamp는 RCT 0 이하 가정용 안전장치)
        Assert.That(CombatMath.PerceptionDelay(Avg with { Rct = 150 }), Is.EqualTo(0.08f).Within(1e-4));
        Assert.That(CombatMath.PerceptionDelay(Avg with { Rct = 1 }),   Is.EqualTo(0.298f).Within(1e-4));
    }

    // ── 데미지 공식 ──

    [Test]
    public void RawDamage_SwordHeavy_Atk70()
    {
        // 42 × 1.5 × 1.7 = 107.1
        float raw = CombatMath.RawDamage(WeaponTable.Sword, C.MotionMultHeavy, Avg);
        Assert.That(raw, Is.EqualTo(107.1f).Within(1e-3));
    }

    [Test]
    public void RawDamage_DualBlades_CountsBothHits()
    {
        // 26 × 2타 × 0.7 × 1.7 = 61.88
        float raw = CombatMath.RawDamage(WeaponTable.DualBlades, C.MotionMultLight, Avg);
        Assert.That(raw, Is.EqualTo(26f * 2f * 0.7f * 1.7f).Within(1e-3));
    }

    [Test]
    public void Mitigation_Def70_IsMultiplicativeNotSubtractive()
    {
        // 100 / (100 + 56) = 0.6410... — 승산곡선이므로 0데미지가 절대 없음
        Assert.That(CombatMath.Mitigation(70, C), Is.EqualTo(100f / 156f).Within(1e-5));
        // 극단 DEF 150이어도 피해는 0이 아니다 (상성 보존)
        Assert.That(CombatMath.Mitigation(150, C), Is.GreaterThan(0.4f));
    }

    [Test]
    public void FinalDamage_Baseline_SwordHeavy()
    {
        // 107.1 × (100/156) = 68.654...
        float dmg = CombatMath.FinalDamage(
            WeaponTable.Sword, C.MotionMultHeavy, Avg, Avg, CombatMath.HitContext.Clean, C);
        Assert.That(dmg, Is.EqualTo(107.1f * 100f / 156f).Within(1e-3));
    }

    [Test]
    public void FinalDamage_MultipliersStack()
    {
        var ctx = new CombatMath.HitContext(IsCrit: true, IsGuarded: false, IsCounter: true,
                                            IsInnerRange: false, EmotionMult: 1.0f, VarianceRoll: 1.0f);
        float clean = CombatMath.FinalDamage(WeaponTable.Sword, 1.5f, Avg, Avg, CombatMath.HitContext.Clean, C);
        float boosted = CombatMath.FinalDamage(WeaponTable.Sword, 1.5f, Avg, Avg, ctx, C);
        Assert.That(boosted, Is.EqualTo(clean * 1.6f * 1.35f).Within(1e-3));
    }

    [Test]
    public void FinalDamage_Guarded_Is25Percent()
    {
        var guarded = CombatMath.HitContext.Clean with { IsGuarded = true };
        float clean = CombatMath.FinalDamage(WeaponTable.Sword, 1.5f, Avg, Avg, CombatMath.HitContext.Clean, C);
        float dmg = CombatMath.FinalDamage(WeaponTable.Sword, 1.5f, Avg, Avg, guarded, C);
        Assert.That(dmg, Is.EqualTo(clean * 0.25f).Within(1e-3));
    }

    [Test]
    public void FinalDamage_WhipBypassesGuardPartially()
    {
        // 채찍 GuardBypass 0.1 → 가드 배율 0.25 + 0.75×0.1 = 0.325
        Assert.That(CombatMath.GuardDamageMult(WeaponTable.Whip, C), Is.EqualTo(0.325f).Within(1e-4));
        Assert.That(CombatMath.GuardDamageMult(WeaponTable.Sword, C), Is.EqualTo(0.25f).Within(1e-4));
    }

    [Test]
    public void FinalDamage_InnerRangePenalty_HurtsSpear()
    {
        // 사거리 안쪽 침투당한 창: ×0.6 — 거리 싸움이 곧 데미지 싸움
        var inner = CombatMath.HitContext.Clean with { IsInnerRange = true };
        float normal = CombatMath.FinalDamage(WeaponTable.Spear, 1.5f, Avg, Avg, CombatMath.HitContext.Clean, C);
        float penalized = CombatMath.FinalDamage(WeaponTable.Spear, 1.5f, Avg, Avg, inner, C);
        Assert.That(penalized, Is.EqualTo(normal * 0.6f).Within(1e-3));
    }

    // ── 치명타 ──

    [Test]
    public void CritChance_EqualStats_Is5Percent()
        => Assert.That(CombatMath.CritChancePct(Avg, Avg, C), Is.EqualTo(5f).Within(1e-4));

    [Test]
    public void CritChance_ClampsLow()
    {
        var weak = Avg with { Atk = 1 };
        var tank = Avg with { Def = 150 };
        Assert.That(CombatMath.CritChancePct(weak, tank, C), Is.EqualTo(2f).Within(1e-4));
    }

    [Test]
    public void CritChance_UpperCap_KnownUnreachable_TuningNote()
    {
        // 알려진 이슈: 계수 0.05에서는 최대 격차(ATK150 vs DEF1)도 12.45%라 상한 20% 도달 불가.
        // M3 튜닝 대상(T06). 이 테스트는 현재 값의 '문서화' 역할 — 계수를 바꾸면 여기가 깨져서 알려준다.
        var max = Avg with { Atk = 150 };
        var min = Avg with { Def = 1 };
        Assert.That(CombatMath.CritChancePct(max, min, C), Is.EqualTo(12.45f).Within(1e-3));
    }
}
