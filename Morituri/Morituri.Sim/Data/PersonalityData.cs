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
    OppStaggeredWhileAhead,       // 상대 스태거 인지 & 내가 우세 (오만함이 방심하는 순간 — 상대는 살아있어 처벌 가능)
    WasTaunted,                   // 최근 value초 이내 상대에게 도발당함 — 성격별 분노/위축 반응
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
    bool OppExhaustedPerceived = false,
    bool OppStaggeredPerceived = false,
    float SecSinceTaunted = 999f);// 상대에게 도발당한 후 경과 초 (작을수록 막 도발당함)

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
        // 상대 스태거 + HP 리드 ≥ CondValue%p: 도발의 조건부 역전패는 처벌 '크기'가 아니라 '어떤 경기 상태가
        //   도발 자격을 얻나'(선별)가 결정한다(M3-B 진단: 배수·창 길이·쿨다운 모두 무효과). 리드 폭이 클수록
        //   넉넉히 앞선 경기만 골라 역전패율이 내려간다. CondValue가 5~10% 밴드를 조이는 유일한 레버.
        TriggerCondition.OppStaggeredWhileAhead   => c.OppStaggeredPerceived
                                                     && (c.SelfHpPct - c.OppHpPct) * 100f >= r.CondValue - Eps,
        TriggerCondition.StaminaNearReserve       => c.StaminaPct <= c.ReservePct + 0.10f + Eps,
        TriggerCondition.SameAttackWhiffedTwice   => c.SameWhiffCount >= (int)r.CondValue,
        TriggerCondition.HpDeficitPct             => c.HpDeficitPct * 100f >= r.CondValue - Eps,
        TriggerCondition.WasTaunted               => c.SecSinceTaunted <= r.CondValue + Eps,
        TriggerCondition.OppIsNemesis             => false, // Phase 2
        _ => false,
    };
}

// ───────────────────────── T05_Personalities ─────────────────────────

public sealed record PersonalityDef(
    string Id,
    float GlobalProbMod,      // 냉철함 -0.5: 모든 트리거 발동 확률 ×(1+값)
    ParamMod[] GlobalMods,    // 상시 파라미터 보정
    TriggerRule[] Rules,
    float CrowdSensitivity = 0f); // 관중 외면(불리) 시 위축 강도 0~1. 쇼맨·오만·겁쟁이만 >0, 그 외는 무관.

/// <summary>성격 12종 (문서[3] 5.2 테이블의 코드화).</summary>
public static class PersonalityTable
{
    private static ParamMod Set(TParam p, float v) => ParamMod.Set(p, v);
    private static ParamMod Add(TParam p, float v) => ParamMod.Add(p, v);
    private static readonly ParamMod[] None = Array.Empty<ParamMod>();

    public static readonly PersonalityDef Calm = new("PER_CALM", -0.5f,
        new[] { Add(TParam.CommitThreshold, 0.10f) }, new[]
    {
        // 도발 면역: 분노 베이스(A)를 상쇄. GlobalProbMod -0.5로 실발동 ~50% — 냉철도 완벽 무시는 아니다.
        new TriggerRule("TRG_CALM_TAUNT", TriggerCondition.WasTaunted, 5f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, -0.25f), Add(TParam.CommitThreshold, 0.10f) },
            InterruptAction.None, 1.0f, 6f, 5f, "COOL"),
        // 전술적 도발: 우세할 때 상대를 자극해 무리수 유도. GlobalProbMod -0.5 → 실발동 ~0.11 (낮은 빈도, 계산적)
        new TriggerRule("TRG_CALM_ACT_TAUNT", TriggerCondition.SelfHpAbovePctAndWinning, 65f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.22f, 15f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef Reckless = new("PER_RECKLESS", 0f, None, new[]
    {
        new TriggerRule("TRG_RECK_HP30", TriggerCondition.SelfHpBelowPct, 30f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.9f), Set(TParam.GuardBias, 0f), Set(TParam.PreferredDistance, 0.6f) },
            InterruptAction.None, 0.80f, 15f, 10f, "RECKLESS"),
        new TriggerRule("TRG_RECK_HITS3", TriggerCondition.ConsecHitsTaken, 3f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, 0.3f) }, InterruptAction.None, 0.60f, 8f, 5f, "RECKLESS"),
        // 도발에 이성을 잃는다 — 분노 베이스(A) 위에 공격성을 더 얹어 무모하게 달려든다.
        new TriggerRule("TRG_RECK_TAUNT", TriggerCondition.WasTaunted, 5f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, 0.3f) }, InterruptAction.None, 0.85f, 6f, 5f, "ENRAGED"),
        // 분노형 도발: 맞으면 "이게 다야?" — 피격 분노가 즉각 도발로 폭발 (3회로 빈도 조절, ConsecHits는 3초 무피격 시 리셋)
        new TriggerRule("TRG_RECK_ACT_TAUNT", TriggerCondition.ConsecHitsTaken, 3f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.45f, 12f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef Arrogant = new("PER_ARROGANT", 0f, None, new[]
    {
        // M3-B 재설계: 빈사(HP≤10%) 트리거는 처벌자가 이미 죽어 역전 0%였다.
        // "우세 + 상대 스태거"로 이동 — 상대는 살아서 스태거(0.8s) 회복 후 도발(1.5s) 잔여 창에 2배 처벌 가능.
        new TriggerRule("TRG_ARRO_TAUNT", TriggerCondition.OppStaggeredWhileAhead, 8f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.70f, 15f, 1.5f, "TAUNT"),
        new TriggerRule("TRG_ARRO_LAZY", TriggerCondition.SelfHpAbovePctAndWinning, 80f, TriggerEffectKind.Override,
            new[] { Add(TParam.CommitThreshold, -0.15f) }, InterruptAction.None, 0.40f, 6f, 5f, "TAUNT"),
        new TriggerRule("TRG_ARRO_ACT_TAUNT", TriggerCondition.OppHpBelowPct, 50f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.50f, 45f, 1.5f, "TAUNT"),
    }, 0.8f);  // 관중 외면 시 위축(자존심에 금)

    public static readonly PersonalityDef Honorable = new("PER_HONORABLE", 0f, None, new[]
    {
        // 고결함: 상대 다운 → 거리 벌리고 대기 (추가타 금지). 관중 호응 트리거는 Phase 3(관중 시스템)로 이연.
        new TriggerRule("TRG_HONOR_HOLD", TriggerCondition.OppDown, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.HoldOff, 0.95f, 2f, 2.0f, "HONOR"),
        // 경의의 도발: 크게 앞설 때 드물게 상대에 경의를 표함 (오만이 아닌 존중의 제스처)
        new TriggerRule("TRG_HONOR_ACT_TAUNT", TriggerCondition.SelfHpAbovePctAndWinning, 80f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.12f, 20f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef Coward = new("PER_COWARD", 0f, None, new[]
    {
        new TriggerRule("TRG_COW_DODGE", TriggerCondition.OppHeavyWindup, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.DodgeBack, 0.65f, 2f, 0f, "FEAR"),
        new TriggerRule("TRG_COW_CRIT", TriggerCondition.CritTakenWithinSec, 8f, TriggerEffectKind.Override,
            new[] { Add(TParam.PreferredDistance, 1.0f), Add(TParam.Aggression, -0.3f) },
            InterruptAction.None, 0.90f, 8f, 8f, "FEAR"),
        // 도발에 분노가 아니라 위축으로 반응 — 분노 베이스(A)를 뒤집어 오히려 물러난다 (도발자가 득보는 유일한 상대).
        new TriggerRule("TRG_COW_TAUNT", TriggerCondition.WasTaunted, 5f, TriggerEffectKind.Override,
            new[] { Add(TParam.PreferredDistance, 0.6f), Add(TParam.Aggression, -0.5f) },
            InterruptAction.None, 0.80f, 6f, 5f, "FLINCH"),
        // 겁쟁이 도발: 압도적으로 유리할 때만 아주 드물게 (안전 확보 후 허세)
        new TriggerRule("TRG_COW_ACT_TAUNT", TriggerCondition.SelfHpAbovePctAndWinning, 90f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.07f, 20f, 1.5f, "TAUNT"),
    }, 0.7f);  // 관중 야유 = 더 겁먹음

    // 쇼맨: 화려한 마무리를 고집하고 우세할 때 관중 반응으로 더 대담해진다. (관중 시스템 없는 Phase 1에서는 자신감 버프로 모델링)
    public static readonly PersonalityDef Showman = new("PER_SHOWMAN", 0f,
        new[] { Add(TParam.HeavyBias, 0.15f) }, new[]
    {
        // 상대 빈사 → 강공 마무리 고집 (극적인 피니시)
        new TriggerRule("TRG_SHOW_FINISH", TriggerCondition.OppHpBelowPct, 25f, TriggerEffectKind.Interrupt,
            None, InterruptAction.ForcedHeavy, 0.75f, 8f, 0f, "SHOWTIME"),
        // 크게 앞선 상황 → 화려한 강공 선호 (관중 열기 = 자신감)
        new TriggerRule("TRG_SHOW_MOMENTUM", TriggerCondition.SelfHpAbovePctAndWinning, 70f, TriggerEffectKind.Override,
            new[] { Add(TParam.HeavyBias, 0.35f), Add(TParam.CommitThreshold, -0.10f) },
            InterruptAction.None, 0.65f, 10f, 6f, "SHOWTIME"),
        // 쇼맨 도발 A: 쓰러진 상대 조롱 — 극적 연출. OppDown은 순간 상태(1.5s)라 자주 안 생겨 쿨타임 짧아도 안전.
        new TriggerRule("TRG_SHOW_TAUNT_DOWN", TriggerCondition.OppDown, 0f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.55f, 6f, 1.5f, "TAUNT"),
        // 쇼맨 도발 B: 피니시 직전 우월감 퍼포먼스. OppHpBelowPct는 죽을 때까지 지속 참 → 긴 쿨타임(30s)으로 스팸 차단.
        new TriggerRule("TRG_SHOW_TAUNT_LOW", TriggerCondition.OppHpBelowPct, 25f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.50f, 30f, 1.5f, "TAUNT"),
    }, 1.0f);  // 관중이 곧 동력 — 외면받으면 가장 크게 위축

    // 기회주의자: 평소엔 방어적·신중하다가 상대의 정량 허점(가드 붕괴 임박·스태미나 고갈)에 폭발적으로 반응.
    //   잔혹함이 감정적 약자 사냥이라면, 기회주의는 계산적 허점 공략.
    public static readonly PersonalityDef Opportunist = new("PER_OPPORTUNIST", 0f,
        new[] { Add(TParam.CommitThreshold, 0.15f), Add(TParam.GuardBias, 0.10f) }, new[]
    {
        // 가드 붕괴 직전 → 강공으로 가드 깨고 스태거 노림
        new TriggerRule("TRG_OPP_GUARD", TriggerCondition.OppGuardGaugeBelowPct, 35f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.90f), Add(TParam.HeavyBias, 0.60f) },
            InterruptAction.None, 0.85f, 6f, 4f, "EXPLOIT"),
        // 스태미나 고갈 → 확정 처벌 창 인지·즉각 공격
        new TriggerRule("TRG_OPP_TIRED", TriggerCondition.OppExhausted, 0f, TriggerEffectKind.Override,
            new[] { Set(TParam.Aggression, 0.85f), Add(TParam.HeavyBias, 0.40f) },
            InterruptAction.None, 0.85f, 6f, 4f, "EXPLOIT"),
        // 가드 붕괴 직전 도발: 허점을 노출하고 심리 교란 (약한 도발 빈도, 계산적 타이밍)
        new TriggerRule("TRG_OPP_ACT_TAUNT", TriggerCondition.OppGuardGaugeBelowPct, 25f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.15f, 15f, 1.5f, "TAUNT"),
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
        // 약자 도발: 상대 HP 30% 이하 — 심리 붕괴 유도 (잔혹함의 핵심 즐거움).
        // OppHpBelowPct는 죽을 때까지 지속 참 → 긴 쿨타임(30s)으로 스팸 차단(cd 10s일 때 경기당 6.6회 스팸 확인).
        new TriggerRule("TRG_CRUEL_TAUNT", TriggerCondition.OppHpBelowPct, 30f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.40f, 30f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef Bold = new("PER_BOLD", 0f, None, new[]
    {
        new TriggerRule("TRG_BOLD", TriggerCondition.TimeRemainPctBelowAndLosing, 20f, TriggerEffectKind.Override,
            new[] { Set(TParam.RiskTolerance, 1.0f), Add(TParam.HeavyBias, 1.0f) },
            InterruptAction.None, 0.85f, 999f, 999f, "BOLD"),
        // 역전 도발: 지고 있을 때 "아직 안 끝났다" — 기백 도발로 상대를 흔들려는 시도
        new TriggerRule("TRG_BOLD_TAUNT", TriggerCondition.HpDeficitPct, 20f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.22f, 15f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef Wary = new("PER_WARY", 0f,
        new[] { Add(TParam.CommitThreshold, 0.20f) }, new[]
    {
        new TriggerRule("TRG_WARY_STAM", TriggerCondition.StaminaNearReserve, 0f, TriggerEffectKind.Override,
            new[] { Add(TParam.Aggression, -0.3f) }, InterruptAction.None, 1.0f, 1f, 3f, "WARY"),
        // 신중한 도발: 압도적 우세 + 위험 없을 때만 (거의 안 함)
        new TriggerRule("TRG_WARY_TAUNT", TriggerCondition.SelfHpAbovePctAndWinning, 90f, TriggerEffectKind.Interrupt,
            None, InterruptAction.Taunt, 0.07f, 25f, 1.5f, "TAUNT"),
    });

    public static readonly PersonalityDef[] All =
        { Calm, Reckless, Arrogant, Honorable, Coward, Cruel, Bold, Wary, Showman, Opportunist };

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
