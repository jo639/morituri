using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

public enum FighterState
{
    Idle, Move, Windup, Active, Recovery,
    Guard, Dodge,
    HitStun, Stagger, Down, GetUp,
    Taunt, // 오만함 전용 — 역전패 제조기 (의도된 설계)
}

public enum ActionRequest { None, Approach, Retreat, Strafe, AttackLight, AttackHeavy, Guard, Dodge, Feint }

/// <summary>지속형 Override의 런타임 인스턴스 (스택, 만료 시 제거 후 재합성 = 롤백).</summary>
public sealed class ActiveOverride
{
    public required ParamMod[] Mods;
    public required float ExpiresAt;
    public required string ReasonTag;
}

/// <summary>
/// 선수 1명의 런타임 전체 = 문서[3] 3장 Blackboard.
/// 순수 데이터 + 파생 스탯 캐시. 로직은 MatchSim이 담당 (테스트 용이성).
/// </summary>
public sealed class FighterRuntime
{
    public required int Index { get; init; }
    public required FighterDef Def { get; init; }
    public required TacticsProfile Profile { get; init; }
    public required PersonalityDef Personality { get; init; }
    public required WeaponDef Weapon { get; init; }

    // --- 파생 스탯 캐시 ---
    public float HpMax, StaminaMax, PoiseMax, GuardGaugeMax, MoveSpeed, PerceptDelaySec;

    // --- 자원 ---
    public float Hp, Stamina, Poise, GuardGauge;
    public bool GuardDisabled;          // GuardBreak 후 게이지 50% 회복 전까지
    public float ExhaustTimer;          // > 0 = Exhausted
    public float PoiseRegenBlockTimer;  // 피격 후 1초 회복 정지

    // --- 위치 (Phase 1 단순화: 1D 라인 아레나. 2D는 M4 프레젠테이션에서) ---
    public float Position;

    // --- FSM ---
    public FighterState State = FighterState.Idle;
    public float StateTimer, StateElapsed;
    public ActionRequest CurrentAction = ActionRequest.None;
    public MotionDef Motion;
    public MotionKind MotionKindNow;
    public bool IsFeintSwing, SwingResolved;
    public float WindupTotalSec;

    // --- 전략층 상태 ---
    public Directive Dir;                                  // 합성된 유효 지시
    public readonly List<ActiveOverride> Overrides = new();
    public readonly Dictionary<string, float> CooldownUntil = new();
    public ActionRequest PendingForced = ActionRequest.None; // ForcedHeavy 인터럽트

    // --- 누적 컨텍스트 (문서[3] 3장) ---
    public int ConsecHitsTaken;
    public float NoHitTimer;            // 3초 무피격 시 ConsecHitsTaken 리셋
    public float LastCritTakenAt = -999f;
    public ActionRequest LastAttack = ActionRequest.None;
    public ActionRequest LastWhiffed = ActionRequest.None;
    public int SameWhiffCount;
    public bool DownHitConsumed;        // 다운 중 추가타 1회 제한

    // --- 통계 (판정 점수 + 검증 지표) ---
    public float DamageDealt, CornerTime;
    public int CleanHits, Knockdowns, AttackAttempts, Whiffs;
    public float MinHpPct = 1f;
    public bool EverTaunted;

    public float HpPct => Hp / HpMax;
    public float StaminaPct => Stamina / StaminaMax;
    public bool IsExhausted => ExhaustTimer > 0f;
    public bool IsAttackSwing => State is FighterState.Windup or FighterState.Active or FighterState.Recovery;

    /// <summary>프로파일 + 성격 상시보정 + 활성 Override 합성 (만료분 제거 = 자동 롤백).</summary>
    public void RebuildDirective(float now)
    {
        Overrides.RemoveAll(o => o.ExpiresAt <= now);
        Dir = Directive.From(Profile);
        foreach (var m in Personality.GlobalMods) Dir.Apply(m);
        foreach (var o in Overrides)
            foreach (var m in o.Mods) Dir.Apply(m);
    }
}

/// <summary>인지 지연용 스냅샷 (문서[3] 6.3) — 상대는 이걸 통해서만 보인다.</summary>
public readonly record struct PerceptSnap(
    FighterState State, MotionKind MotionKind, float Position,
    float HpPct, float GuardGaugePct, float StateElapsed, bool IsExhausted);
