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
    public float Patience;              // 인내심 0~Max. 무교전(NoHitTimer)이 길수록 감소 → 공격 충동↑. 영원 대치 해소.
    public bool GuardDisabled;          // GuardBreak 후 게이지 50% 회복 전까지
    public float ExhaustTimer;          // > 0 = Exhausted
    public float PoiseRegenBlockTimer;  // 피격 후 1초 회복 정지

    // --- 출혈 (별도 트랙, 문서[7]§2) — 매 틱 BleedStacks×BleedDps 피해 ---
    public int   BleedStacks;
    public float BleedDps;              // 최근 적용 무기의 스택당 dps
    public float BleedExpiry;           // 만료 시각 (now ≥ 이 값이면 소멸)
    public int   BleedSource = -1;      // 출혈 입힌 공격자 index (피해 귀속)

    // --- 패링당함 스택 (방패 패링 한정 + 감쇠) — 내 공격이 패링당한 누적, 임계 도달 시 내가 기절 ---
    public int   ParriedStacks;
    public float ParriedDecayAt;        // 다음 1스택 감쇠 시각

    // --- 특성 (T09, 문서[7]§6) — 생성 시 CreateRuntime이 파생스탯에 반영 + 고유 행동은 보유 여부로 분기 ---
    public HashSet<string> Traits = new();
    public float DamageTakenMult = 1f;  // 받는 피해 배율 (유리몸·질긴가죽·둔감)
    public float GuardDamageMult = 1f;  // 가드 시 받는 피해 추가 배율 (봉쇄자)
    public float StamRegenTraitMult = 1f; // 스태미나 회복 배율 (마르지않는샘·허약체질)
    public float DodgeCostMult = 1f;    // 대시 스태미나 소모 배율 (초상비)
    public float RangeBonus;            // 사거리 가산(flat)
    public float RangeMult = 1f;        // 사거리 배율 (거인 — 비례 reach, 무기 정체성 보존)
    public float SizeScale = 1f;        // 뷰어 크기 배율 (거인) — sim 무관, 프레젠테이션
    public float DashSpeedBuffUntil;    // 초상비: 대시 후 이속 버프 만료
    public bool  FirstHitDone;          // 선취점: 첫 클린 히트 소비 여부
    public float ShieldHp;              // 흡수 쉴드 잔량 (선취점·향후 액티브)
    public float ShieldExpiry;          // 흡수 쉴드 만료

    public bool Has(string traitId) => Traits.Contains(traitId);
    public float EffRange => Weapon.Range * RangeMult + RangeBonus;   // 유효 사거리 (거인: ×RangeMult 비례)

    // --- 위치 (B: 2D-lite. 원형 핏, 중심 0,0. 문서[8]) ---
    public Vec2 Pos;
    public Vec2 PrevPos;                // 이번 틱 이동 직전 위치 — disc 충돌이 "누가 파고들었나"를 가리는 데 사용
    public float CircleSign = 1f;       // 선회 방향 (+1 반시계 / -1 시계). 경계 도달 시 반전

    // --- 관중 기세 (문서[10]) — MatchSim이 군중게이지로부터 매 틱 갱신 ---
    public float CrowdMomentum;         // 0~1: 관중이 내 편(유리). 보편 기세 버프(공격성·커밋·회복·데미지·이속).
    public float CrowdPressure;         // 0~1: 관중이 상대 편(불리). 위축은 성격 민감도(CrowdSensitivity)에 비례.

    // --- FSM ---
    public FighterState State = FighterState.Idle;
    public float StateTimer, StateElapsed;
    public ActionRequest CurrentAction = ActionRequest.None;
    public MotionDef Motion;
    public MotionKind MotionKindNow;
    public bool IsFeintSwing, SwingResolved;
    public bool LastSwingGuarded; // 이번 스윙이 가드됨 → 후딜 ×GuardedRecoveryMult (프레임 불리)
    public float WindupTotalSec;

    // --- 전략층 상태 ---
    public Directive Dir;                                  // 합성된 유효 지시
    public readonly List<ActiveOverride> Overrides = new();
    public readonly Dictionary<string, float> CooldownUntil = new();
    public ActionRequest PendingForced = ActionRequest.None; // ForcedHeavy 인터럽트
    public float RepositionUntil;        // [안B] 공격 후 이탈 창 — 이 시각까지 후퇴 강제(카이터 찌르고 빠짐)

    // --- 누적 컨텍스트 (문서[3] 3장) ---
    public int ConsecHitsTaken;
    public float NoHitTimer;            // 3초 무피격 시 ConsecHitsTaken 리셋
    public float LastCritTakenAt = -999f;
    public float LastTauntedAt = -999f;  // 상대에게 도발당한 시각 — 분노 반응(성격별) 트리거용
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
        // 관중 기세(유리, 보편): 적극·결단·지구력 ↑. (데미지·이속은 MatchSim에서 CrowdMomentum 직접 사용)
        if (CrowdMomentum > 0f)
        {
            Dir.Apply(ParamMod.Add(TParam.Aggression, 0.30f * CrowdMomentum));
            Dir.Apply(ParamMod.Add(TParam.CommitThreshold, -0.10f * CrowdMomentum));
            Dir.Apply(ParamMod.Add(TParam.StamRegenMult, 0.30f * CrowdMomentum));
        }
        // 관중 외면(불리): 기본 무효, 성격 민감도만큼만 위축(공격성↓·결단↓) — 쇼맨·오만·겁쟁이만 반응.
        if (CrowdPressure > 0f && Personality.CrowdSensitivity > 0f)
        {
            float p = CrowdPressure * Personality.CrowdSensitivity;
            Dir.Apply(ParamMod.Add(TParam.Aggression, -0.25f * p));
            Dir.Apply(ParamMod.Add(TParam.CommitThreshold, 0.10f * p));
        }
    }
}

/// <summary>인지 지연용 스냅샷 (문서[3] 6.3) — 상대는 이걸 통해서만 보인다.</summary>
public readonly record struct PerceptSnap(
    FighterState State, MotionKind MotionKind, Vec2 Pos,
    float HpPct, float GuardGaugePct, float StateElapsed, bool IsExhausted);
