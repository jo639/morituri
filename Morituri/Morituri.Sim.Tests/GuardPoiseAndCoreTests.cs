using Morituri.Sim.Combat;
using Morituri.Sim.Core;
using Morituri.Sim.Data;

namespace Morituri.Sim.Tests;

/// <summary>문서[4] 4~5장: 가드 게이지와 Poise/Stagger 검증.</summary>
[TestFixture]
public class GuardAndPoiseTests
{
    private static readonly BalanceConstants C = BalanceConstants.Default;
    private static readonly FighterStats Avg = FighterStats.Baseline;

    [Test]
    public void Guard_ReducesGaugeAndStamina_ByRawDamage()
    {
        // 도끼 강공 Raw = 64×1.5×1.7 = 163.2
        float raw = CombatMath.RawDamage(WeaponTable.Axe, C.MotionMultHeavy, Avg);
        var r = CombatMath.ResolveGuardHit(raw, WeaponTable.Axe, guardGauge: 100f, stamina: 95f, C);

        Assert.That(r.GuardGaugeAfter, Is.EqualTo(100f - raw * 0.55f).Within(1e-3)); // GuardCrush 0.55
        Assert.That(r.StaminaAfter, Is.EqualTo(95f - raw * C.GuardStaminaCostRatio).Within(1e-3));
        Assert.That(r.IsGuardBreak, Is.False);
    }

    [Test]
    public void Guard_AxeHeavy_BreaksAverageGuardInOneOrTwoHits()
    {
        // 도끼의 존재 이유: 평균 선수(게이지 68)는 도끼 강공 1방(89.76 깎임)에 가드가 깨진다
        float raw = CombatMath.RawDamage(WeaponTable.Axe, C.MotionMultHeavy, Avg);
        float gaugeMax = CombatMath.GuardGaugeMax(Avg, WeaponTable.Sword, C); // 68
        var r = CombatMath.ResolveGuardHit(raw, WeaponTable.Axe, gaugeMax, 95f, C);

        Assert.That(r.IsGuardBreak, Is.True);
        Assert.That(r.StaggerSec, Is.EqualTo(1.2f).Within(1e-4));
        Assert.That(r.GuardGaugeAfter, Is.EqualTo(0f)); // 음수로 내려가지 않음
    }

    [Test]
    public void Guard_WhipBarelyScratchesGauge()
    {
        // 채찍 GuardCrush 0.10 — 가드 깎기로는 방패검을 못 연다 (가드 우회가 채찍의 길)
        float raw = CombatMath.RawDamage(WeaponTable.Whip, C.MotionMultHeavy, Avg); // 30×1.5×1.7=76.5
        float shieldGauge = CombatMath.GuardGaugeMax(Avg, WeaponTable.SwordShield, C); // 108.8
        var r = CombatMath.ResolveGuardHit(raw, WeaponTable.Whip, shieldGauge, 95f, C);

        Assert.That(r.IsGuardBreak, Is.False);
        Assert.That(shieldGauge - r.GuardGaugeAfter, Is.LessThan(shieldGauge * 0.1f)); // 1대당 10% 미만
    }

    [Test]
    public void Poise_HammerHeavy_StaggersAveragePoiseInOneHit()
    {
        // 망치의 정체성: PoiseDmg 45 × 1.5 = 67.5 → Poise 60 기준 1방 Stagger
        var r = CombatMath.ApplyPoiseDamage(poise: 60f, poiseMax: 60f,
            WeaponTable.Hammer, C.MotionMultHeavy, defenderExhausted: false, C);

        Assert.That(r.IsStagger, Is.True);
        Assert.That(r.StunSec, Is.EqualTo(0.8f).Within(1e-4));
        Assert.That(r.PoiseAfter, Is.EqualTo(60f)); // Stagger 시 전량 회복
    }

    [Test]
    public void Poise_LightHit_CausesHitStunOnly()
    {
        // 쌍검 약공: PoiseDmg 10×0.7=7 → HitStun = 0.15 + 7×0.004 = 0.178초
        var r = CombatMath.ApplyPoiseDamage(60f, 60f, WeaponTable.DualBlades, C.MotionMultLight, false, C);

        Assert.That(r.IsStagger, Is.False);
        Assert.That(r.PoiseAfter, Is.EqualTo(53f).Within(1e-3));
        Assert.That(r.StunSec, Is.EqualTo(0.15f + 7f * 0.004f).Within(1e-4));
    }

    [Test]
    public void Poise_ExhaustedDefender_Takes150PercentPoiseDamage()
    {
        var normal = CombatMath.ApplyPoiseDamage(60f, 60f, WeaponTable.Sword, 1.0f, false, C);
        var tired  = CombatMath.ApplyPoiseDamage(60f, 60f, WeaponTable.Sword, 1.0f, true, C);
        float dmgNormal = 60f - normal.PoiseAfter;
        float dmgTired  = 60f - tired.PoiseAfter;
        Assert.That(dmgTired, Is.EqualTo(dmgNormal * 1.5f).Within(1e-3));
    }
}

/// <summary>문서[4] 10장: 시간 종료 판정 점수.</summary>
[TestFixture]
public class JudgementScoreTests
{
    private static readonly BalanceConstants C = BalanceConstants.Default;

    [Test]
    public void Score_FormulaMatchesDoc()
    {
        // 데미지 300, 클린히트 5, 다운 1, 유효시도 20, 코너 10초
        // 300 + 40 + 40 + 30 - 20 = 390
        float s = CombatMath.JudgementScore(300f, 5, 1, 20, 10f, C);
        Assert.That(s, Is.EqualTo(390f).Within(1e-3));
    }

    [Test]
    public void Score_AggressionBeatsPassivity_AtEqualDamage()
    {
        // 같은 데미지면 유효 시도(연결된 스윙)가 많고 코너에 덜 몰린 쪽이 이긴다 (판정형 전술의 근거)
        float active  = CombatMath.JudgementScore(200f, 3, 0, 30, 5f, C);
        float passive = CombatMath.JudgementScore(200f, 3, 0, 10, 30f, C);
        Assert.That(active, Is.GreaterThan(passive));
    }
}

/// <summary>아키텍처 원칙 B: 결정론.</summary>
[TestFixture]
public class SimRandomTests
{
    [Test]
    public void SameSeed_ProducesIdenticalSequence()
    {
        var a = new SimRandom(12345);
        var b = new SimRandom(12345);
        for (int i = 0; i < 1000; i++)
            Assert.That(b.NextUInt64(), Is.EqualTo(a.NextUInt64()));
    }

    [Test]
    public void DifferentSeeds_Diverge()
    {
        var a = new SimRandom(1);
        var b = new SimRandom(2);
        Assert.That(a.NextUInt64(), Is.Not.EqualTo(b.NextUInt64()));
    }

    [Test]
    public void VarianceRoll_StaysInDocumentedRange()
    {
        var c = BalanceConstants.Default;
        var rng = new SimRandom(777);
        for (int i = 0; i < 10_000; i++)
        {
            float v = rng.Range(c.VarianceMin, c.VarianceMax);
            Assert.That(v, Is.InRange(0.92f, 1.08f));
        }
    }

    [Test]
    public void NextFloat01_CoversRangeRoughlyUniformly()
    {
        var rng = new SimRandom(42);
        int[] buckets = new int[10];
        const int n = 100_000;
        for (int i = 0; i < n; i++)
            buckets[(int)(rng.NextFloat01() * 10)]++;
        foreach (int count in buckets)
            Assert.That(count, Is.InRange(n / 10 * 0.9, n / 10 * 1.1)); // ±10%
    }

    [Test]
    public void DerivedStream_IsDeterministicToo()
    {
        var a = new SimRandom(99).Derive(1);
        var b = new SimRandom(99).Derive(1);
        Assert.That(b.NextUInt64(), Is.EqualTo(a.NextUInt64()));
    }
}
