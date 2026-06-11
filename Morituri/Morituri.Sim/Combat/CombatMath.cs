using Morituri.Sim.Data;

namespace Morituri.Sim.Combat;

/// <summary>
/// 문서[4] 전투 연산 공식의 순수 함수 구현.
/// 설계 규칙:
///  1. 상태 없음 — 모든 입력은 인자, 모든 계수는 BalanceConstants에서.
///  2. 난수를 내부에서 굴리지 않음 — Variance/치명타 판정 결과는 호출자가 SimRandom으로
///     굴려서 인자로 주입한다. (결정론 원칙 B + 단위 테스트 가능성)
///  3. UnityEngine 참조 금지 (원칙 A).
/// </summary>
public static class CombatMath
{
    // ─────────────────────────── 파생 스탯 (문서[4] 1장) ───────────────────────────

    public static float StaminaMax(in FighterStats s, in BalanceConstants c)
        => c.StaminaMaxBase + s.HpMax * c.StaminaMaxPerHp;

    public static float GuardGaugeMax(in FighterStats s, in WeaponDef w, in BalanceConstants c)
        => (c.GuardGaugeBase + s.Def * c.GuardGaugePerDef) * (1f + w.GuardGaugeBonus);

    /// <summary>이동속도 m/s 환산: 2.0 + SPD×0.02</summary>
    public static float MoveSpeedMps(in FighterStats s, in BalanceConstants c)
        => c.MoveSpeedBase + s.Spd * c.MoveSpeedPerSpd;

    /// <summary>
    /// 실제 모션 시간 = 기본시간 / (무기 모션속도 × (0.7 + ASPD/250)). 문서[4] 7장.
    /// </summary>
    public static float MotionTime(float baseMotionSec, in WeaponDef w, in FighterStats s, in BalanceConstants c)
        => baseMotionSec / (w.MotionSpeed * (c.AspdMotionBase + s.Aspd / c.AspdMotionDiv));

    /// <summary>
    /// 인지 지연 (문서[3] 6.3): clamp(0.30 - RCT×0.002, 0.08, 0.30).
    /// 카운터형 전술 성립의 기계적 기반. 노화로 RCT가 떨어지면 같은 전술인데 카운터가 늦는다.
    /// </summary>
    public static float PerceptionDelay(in FighterStats s)
        => Math.Clamp(0.30f - s.Rct * 0.002f, 0.08f, 0.30f);

    // ─────────────────────────── 데미지 (문서[4] 2장) ───────────────────────────

    public static float RawDamage(in WeaponDef w, float motionMult, in FighterStats attacker)
        => w.BaseDamage * w.HitCount * motionMult * (1f + attacker.Atk / 100f);

    /// <summary>승산곡선: 100 / (100 + DEF×DefCurve). 항상 비례 피해를 보장 (0데미지 없음).</summary>
    public static float Mitigation(float def, in BalanceConstants c)
        => 100f / (100f + def * c.DefCurve);

    /// <summary>치명타 확률(%): 5 + (ATK-DEF)×0.05, clamp 2~20.</summary>
    public static float CritChancePct(in FighterStats attacker, in FighterStats defender, in BalanceConstants c)
        => Math.Clamp(c.CritBase + (attacker.Atk - defender.Def) * c.CritScale,
                      c.CritMinPct, c.CritMaxPct);

    /// <summary>한 번의 적중에 대한 상황 플래그. 판정(크리 여부 등)은 호출자가 먼저 굴린다.</summary>
    public readonly record struct HitContext(
        bool IsCrit,
        bool IsGuarded,
        bool IsCounter,        // 상대가 AttackWindup/Recovery 중 적중
        bool IsInnerRange,     // 자기 사거리보다 안쪽으로 파고든 적 (창/채찍 약점)
        float EmotionMult,     // Phase 2 예약. Phase 1 = 1.0
        float VarianceRoll,    // SimRandom.Range(c.VarianceMin, c.VarianceMax) 결과
        bool DefenderExhausted = false) // 지친 방어자 = 받는 피해 증가 (M3: 난전형 가스아웃 처벌)
    {
        public static readonly HitContext Clean = new(false, false, false, false, 1.0f, 1.0f);
    }

    /// <summary>
    /// FinalDamage = Raw × Mitigation × Crit × Guard × Counter × Emotion × Variance × Exhaust (× InnerRangePenalty)
    /// </summary>
    public static float FinalDamage(
        in WeaponDef weapon, float motionMult,
        in FighterStats attacker, in FighterStats defender,
        in HitContext ctx, in BalanceConstants c)
    {
        float raw = RawDamage(weapon, motionMult, attacker);
        if (ctx.IsInnerRange) raw *= c.InnerRangePenalty;

        return raw
             * Mitigation(defender.Def, c)
             * (ctx.IsCrit    ? c.CritMult    : 1f)
             * (ctx.IsGuarded ? GuardDamageMult(weapon, c) : 1f)
             * (ctx.IsCounter ? c.CounterMult : 1f)
             * (ctx.DefenderExhausted ? c.ExhaustDamageTakenMult : 1f)
             * ctx.EmotionMult
             * ctx.VarianceRoll;
    }

    /// <summary>가드 시 데미지 배율. 채찍은 GuardBypass만큼 가드를 우회 (배율이 그만큼 덜 깎임).</summary>
    public static float GuardDamageMult(in WeaponDef attackerWeapon, in BalanceConstants c)
        => c.GuardDmgMult + (1f - c.GuardDmgMult) * attackerWeapon.GuardBypass;

    // ─────────────────────────── 가드 (문서[4] 4장) ───────────────────────────

    public readonly record struct GuardResult(
        float GuardGaugeAfter,
        float StaminaAfter,
        bool IsGuardBreak,
        float StaggerSec);   // GuardBreak 시 1.2초, 아니면 0

    /// <summary>
    /// 가드 성공 시 게이지/스태미나 처리. (데미지 배율은 FinalDamage의 IsGuarded가 담당)
    /// GuardGauge -= Raw × WeaponGuardCrush, Stamina -= Raw × 0.15
    /// </summary>
    public static GuardResult ResolveGuardHit(
        float rawDamage, in WeaponDef attackerWeapon,
        float guardGauge, float stamina, in BalanceConstants c)
    {
        float gaugeAfter = guardGauge - rawDamage * attackerWeapon.GuardCrush;
        float stamAfter = stamina - rawDamage * c.GuardStaminaCostRatio;
        bool broken = gaugeAfter <= 0f;
        return new GuardResult(
            MathF.Max(0f, gaugeAfter),
            stamAfter,
            broken,
            broken ? c.GuardBreakStaggerSec : 0f);
    }

    // ─────────────────────────── 경직 (문서[4] 5장) ───────────────────────────

    public readonly record struct PoiseResult(
        float PoiseAfter,    // Stagger 발생 시 전량 회복(Max)
        bool IsStagger,
        float StunSec);      // Stagger 0.8초 또는 HitStun

    /// <summary>
    /// 피격 시 Poise 처리: Poise -= PoiseDmg × MotionMult.
    /// Poise ≤ 0 → Stagger + 전량 회복. 아니면 HitStun = 0.15 + PoiseDmg×0.004.
    /// </summary>
    public static PoiseResult ApplyPoiseDamage(
        float poise, float poiseMax,
        in WeaponDef attackerWeapon, float motionMult,
        bool defenderExhausted, in BalanceConstants c)
    {
        float poiseDmg = attackerWeapon.PoiseDamage * motionMult
                       * (defenderExhausted ? c.ExhaustPoiseDmgTakenMult : 1f);
        float after = poise - poiseDmg;

        if (after <= 0f)
            return new PoiseResult(poiseMax, true, c.StaggerSec);

        float stun = c.HitStunBase + poiseDmg * c.HitStunPerPoiseDmg;
        return new PoiseResult(after, false, stun);
    }

    // ─────────────────────────── 판정 점수 (문서[4] 10장) ───────────────────────────

    /// <summary>
    /// 시간 종료 판정 점수:
    /// 가한데미지×1.0 + 클린히트×8 + 다운유발×40 + 유효시도×1.5 - 코너체류초×2
    /// 유효시도 = 공격시도 − 헛스윙. 빈 공간을 가르는 헛스윙은 어그레션으로 치지 않는다
    /// (헛스윙 44회가 점수를 받아 카운터형이 판정 필패하던 결함 수정 — 문서[4] 10장 개정).
    /// </summary>
    public static float JudgementScore(
        float damageDealt, int cleanHits, int knockdownsCaused,
        int effectiveAttempts, float cornerTimeSec, in BalanceConstants c)
        => damageDealt * c.ScorePerDamage
         + cleanHits * c.ScorePerCleanHit
         + knockdownsCaused * c.ScorePerKnockdown
         + effectiveAttempts * c.ScorePerAttackAttempt
         - cornerTimeSec * c.ScorePenaltyPerCornerSec;
}
