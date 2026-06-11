namespace Morituri.Sim.Data;

// ───────────────────────── T04_TriggerRules ─────────────────────────

/// <summary>
/// 트리거 조건 enum (문서[5] 5장: "이 enum 목록이 곧 성격 시스템의 표현력").
/// Phase 2 감정/관계는 여기에 enum 추가로 들어온다 (예: OppIsNemesis가 첫 예약분).
/// </summary>
public enum TriggerCondition
{
    SelfHpBelowPct,               // 자기 HP% ≤ value
    SelfHpAbovePctAndWinning,     // 자기 HP% ≥ value & HP 우세
    OppHpBelowPct,                // 상대(인지된) HP% ≤ value
    ConsecHitsTaken,              // 연속 피격 ≥ value
    OppHeavyWindup,               // 상대 강공 선딜 인지 (인지 지연 적용됨)
    OppDown,                      // 상대 다운 상태 인지
    CritTakenWithinSec,           // 치명타 피격 후 value초 이내
    TimeRemainPctBelowAndLosing,  // 남은 시간% ≤ value & HP 열세
    OppGuardGaugeBelowPct,        // 상대 가드 게이지% ≤ value
    OppExhausted,                 // 상대 스태미나 고갈 인지 (취약 상태 — 잔혹함이 노리는 틈)
    StaminaNearReserve,           // 스태미나 ≤ Reserve + 10%p
    SameAttackWhiffedTwice,       // 같은 공격 연속 빗나감 ≥ value
    HpDeficitPct,                 // HP 열세 차이 ≥ value%p
    OppIsNemesis,                 // Phase 2 예약 (관계 시스템) — Phase 1에서는 항상 false
}

public enum TriggerEffectKind { Override, Interrupt }

public enum InterruptAction { None, Taunt, DodgeBack, ForcedHeavy, HoldOff }

/// <summary>성격 트리거와 전술 고유 조건이 같은 스키마를 쓴다 (문서[5] 5장, 엔진 단일화).</summary>
public sealed record TriggerRule(
    string Id,
    TriggerCondition Cond,
    float CondValue,
    TriggerEffectKind Kind,
    ParamMod[] Mods,              // Override용
    InterruptAction Interrupt,    // Interrupt용
    float Probability,
    float CooldownSec,
    float DurationSec,
    string ReasonTag);

/// <summary>트리거 평가에 필요한 상황 스냅샷 (인지 지연이 반영된 값들).</summary>
public readonly record struct TriggerContext(
    float SelfHpPct,
    float OppHpPct,               // 인지된 값
    bool SelfWinning,             // HP 우세
    int ConsecHitsTaken,
    bool OppHeavyWindupPerceived,
    bool OppDownPerceived,
    float SecSinceCritTaken,
    float TimeRemainPct,
    float OppGuardGaugePct,
    float StaminaPct,
    float ReservePct,
    int SameWhiffCount,
    float HpDeficitPct,           // 상대HP% - 자기HP% (양수 = 내가 열세)
    bool OppExhaustedPerceived = false);

public static class TriggerEval
{
    private const float Eps = 1e-3f; // float 누적 오차 보호 (0.30f×100 = 30.000002 문제)

    public static bool Matches(TriggerRule r, in TriggerContext c) => r.Cond switch
    {
        TriggerCondition.SelfHpBelowPct           => c.SelfHpPct * 100f <= r.CondValue + Eps,
        TriggerCondition.SelfHpAbovePctAndWinning => c.SelfHpPct * 100f >= r.CondValue - Eps && c.SelfWinning,
        TriggerCondition.OppHpBelowPct            => c.OppHpPct * 100f <= r.CondValue + Eps,
        TriggerCondition.ConsecHitsTaken          => c.ConsecHitsTaken >= (int)r.CondValue,
        TriggerCondition.OppHeavyWindup           => c.OppHeavyWindupPerceived,
        TriggerCondition.OppDown                  => c.OppDownPerceived,
        TriggerCondition.CritTakenWithinSec       => c.SecSinceCritTaken <= r.CondValue + Eps,
        TriggerCondition.TimeRemainPctBelowAndLosing => c.TimeRemainPct * 100f <= r.CondValue + Eps && c.HpDeficitPct > 0f,
        TriggerCondition.OppGuardGaugeBelowPct    => c.OppGuardGaugePct * 100f <= r.CondValue + Eps,
        TriggerCondition.OppExhausted             => c.OppExhaustedPerceived,
        TriggerCondition.StaminaNearReserve       => c.StaminaPct <= c.ReservePct + 0.10f + Eps,
        TriggerCondition.SameAttackWhiffedTwice   => c.SameWhiffCount >= (int)r.CondValue,
        TriggerCondition.HpDeficitPct             => c.HpDeficitPct * 100f >= r.CondValue - Eps,
        TriggerCondition.OppIsNemesis             => false, // Phase 2
        _ => false,
    };
}

// ───────────────────────── T05_Personalities ─────────────────────────

public sealed record PersonalityDef(
    string Id,
    float GlobalProbMod,      // 냉철함 -0.5: 모든 트리거 발동 확률 ×(1+값)
    ParamMod[] GlobalMods,    // 상시 파라미터 보정
    TriggerRule[] Rules);

/// <summary>성격 12종 (문서[3] 5.2 테이블의 코드화).</summary>
public static class PersonalityTable
{
    private static ParamMod Set(TParam p, float v) => ParamMod.Set(p, v);
    private static ParamMod Add(TParam p, float v) => ParamMod.Add(p, v);
    private static readonly ParamMod[] None = Array.Empty<ParamMod>();

    public static readonly PersonalityDef Calm = new("PER_CALM", -0.5f,
        new[] { Add(TParam.CommitThreshold, 0.10f) }, Array.Empty<TriggerRule>());

    public static readonly PersonalityDef Reckless = new("PER_RECKLESS", 0f, None, new[]
    {
        new TriggerRule("TRG_RECK_HP30", TriggerCondition.SelfHpBelowPct, 30f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.9f), Set(TParam.GuardBias, 0f), Set(TParam.PreferredDistance, 0.6f) },
            InterruptAction.None, 0.80f, 15f, 10f, "RECKLESS"),
        new TriggerRule("TRG_RECK_HITS3", TriggerCondition.ConsecHitsTaken, 3f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, 0.3f) }, InterruptAction.None, 0.60f, 8f, 5f, "RECKLESS"),
    });

    public static readonly PersonalityDef Arrogant = new("PER_ARROGANT", 0f, None, new[]
    {
        new TriggerRule("TRG_ARRO_TAUNT", TriggerCondition.OppHpBelowPct, 10f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.70f, 10f, 1.5f, "TAUNT"),
        new TriggerRule("TRG_ARRO_LAZY", TriggerCondition.SelfHpAbovePctAndWinning, 80f, TriggerEffectKind.Override,
            new[] { Add(TParam.CommitThreshold, -0.15f) }, InterruptAction.None, 0.40f, 6f, 5f, "TAUNT"),
    });

    public static readonly PersonalityDef Honorable = new("PER_HONORABLE", 0f, None, new[]
    {
        // 상대 다운 → 거리 벌리고 대기 (추가타 금지). 관중 호응 트리거는 Phase 3(관중 시스템)로 이연.
        new TriggerRule("TRG_HONOR_HOLD", TriggerCondition.OppDown, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.HoldOff, 0.95f, 2f, 2.0f, "HONOR"),
    });

    public static readonly PersonalityDef Coward = new("PER_COWARD", 0f, None, new[]
    {
        new TriggerRule("TRG_COW_DODGE", TriggerCondition.OppHeavyWindup, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.DodgeBack, 0.65f, 2f, 0f, "FEAR"),
        new TriggerRule("TRG_COW_CRIT", TriggerCondition.CritTakenWithinSec, 8f, TriggerEffectKind.Override,
            new[] { Add(TParam.PreferredDistance, 1.0f), Add(TParam.Aggression, -0.3f) },
            InterruptAction.None, 0.90f, 8f, 8f, "FEAR"),
    });

    public static readonly PersonalityDef Vengeful = new("PER_VENGEFUL", 0f, None, new[]
    {
        // Phase 2 입력 (관계 시스템). Phase 1에서는 조건이 항상 false — 인터페이스만 존재.
        new TriggerRule("TRG_VENGE", TriggerCondition.OppIsNemesis, 0f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, 0.25f), Add(TParam.RiskTolerance, 0.3f) },
            InterruptAction.None, 1.0f, 5f, 5f, "VENGE"),
    });

    public static readonly PersonalityDef Obsessive = new("PER_OBSESSIVE", 0f, None, new[]
    {
        new TriggerRule("TRG_OBSESS", TriggerCondition.SameAttackWhiffedTwice, 2f, TriggerEffectKind.Override,
            new[] { Add(TParam.RepeatBias, 0.4f) }, InterruptAction.None, 0.70f, 4f, 4f, "OBSESS"),
    });

    public static readonly PersonalityDef Optimist = new("PER_OPTIMIST", 0f, None, new[]
    {
        new TriggerRule("TRG_HOPE", TriggerCondition.SelfHpBelowPct, 20f, TriggerEffectKind.Override,
            new[] { Set(TParam.StamRegenMult, 1.1f) }, InterruptAction.None, 1.0f, 999f, 999f, "HOPE"),
    });

    public static readonly PersonalityDef Pessimist = new("PER_PESSIMIST", 0f, None, new[]
    {
        new TriggerRule("TRG_GLOOM", TriggerCondition.HpDeficitPct, 25f, TriggerEffectKind.Override,
            new[] { Add(TParam.GuardBias, 0.3f), Add(TParam.Aggression, -0.2f) },
            InterruptAction.None, 0.75f, 6f, 6f, "GLOOM"),
    });

    // 잔혹함 = 상대의 '취약 상태'를 냄새 맡고 덮친다. 특정 약점(가드)이 아니라 약함 그 자체가 방아쇠:
    // 가드 붕괴 / 빈사 / 스태미나 고갈 어느 것이든 공격성을 폭발시킨다. (다운은 즉발 추가타.)
    public static readonly PersonalityDef Cruel = new("PER_CRUEL", 0f, None, new[]
    {
        new TriggerRule("TRG_CRUEL_GG", TriggerCondition.OppGuardGaugeBelowPct, 20f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.95f), Add(TParam.HeavyBias, 0.5f) },
            InterruptAction.None, 0.90f, 5f, 5f, "CRUEL"),
        new TriggerRule("TRG_CRUEL_HP", TriggerCondition.OppHpBelowPct, 30f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.95f), Add(TParam.HeavyBias, 0.5f) },
            InterruptAction.None, 0.90f, 5f, 5f, "CRUEL"),
        new TriggerRule("TRG_CRUEL_TIRED", TriggerCondition.OppExhausted, 0f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.95f), Add(TParam.HeavyBias, 0.5f) },
            InterruptAction.None, 0.90f, 5f, 5f, "CRUEL"),
        new TriggerRule("TRG_CRUEL_DOWN", TriggerCondition.OppDown, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.ForcedHeavy, 0.80f, 2f, 0f, "CRUEL"),
    });

    public static readonly PersonalityDef Bold = new("PER_BOLD", 0f, None, new[]
    {
        new TriggerRule("TRG_BOLD", TriggerCondition.TimeRemainPctBelowAndLosing, 20f, TriggerEffectKind.Override,
            new[] { Set(TParam.RiskTolerance, 1.0f), Add(TParam.HeavyBias, 1.0f) },
            InterruptAction.None, 0.85f, 999f, 999f, "BOLD"),
    });

    public static readonly PersonalityDef Wary = new("PER_WARY", 0f,
        new[] { Add(TParam.CommitThreshold, 0.20f) }, new[]
    {
        new TriggerRule("TRG_WARY_STAM", TriggerCondition.StaminaNearReserve, 0f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, -0.3f) }, InterruptAction.None, 1.0f, 1f, 3f, "WARY"),
    });

    public static readonly PersonalityDef[] All =
        { Calm, Reckless, Arrogant, Honorable, Coward, Vengeful, Obsessive, Optimist, Pessimist, Cruel, Bold, Wary };

    public static PersonalityDef Get(string id) => Array.Find(All, p => p.Id == id)!;
}

// ───────────────────────── T07_FighterTemplates ─────────────────────────

/// <summary>테스트용 선수 정의 (스탯 + 무기 + 전술 + 성격 조합).</summary>
public sealed record FighterDef(string Name, FighterStats Stats, string WeaponId, string TacticsId, string PersonalityId)
{
    /// <summary>문서[4] 11장 검증 케이스: 버서커 (난전형 + 충동적 + 도끼)</summary>
    public static readonly FighterDef Berserker =
        new("버서커", FighterStats.Baseline, "WPN_AXE", "TAC_BRAWLER", "PER_RECKLESS");

    /// <summary>문서[4] 11장 검증 케이스: 전술가 (카운터형 + 냉철함 + 창)</summary>
    public static readonly FighterDef Tactician =
        new("전술가", FighterStats.Baseline, "WPN_SPEAR", "TAC_COUNTER", "PER_CALM");
}
