using Morituri.Sim.Data;

namespace Morituri.Sim.Match;

public enum FighterState
{
    Idle, Move, Windup, Active, Recovery,
    Guard, Dodge,
    HitStun, Stagger, Down, GetUp,
    Taunt, // 오만함 전용 — 역전패 제조기 (의도된 설계)
}

public enum ActionRequest { None, Approach, Retreat, Strafe, AttackLight, AttackHeavy, Guard, Dodge, Feint, Hold }

/// <summary>스페이싱 의도(안2) — gap 기반 이동 결정을 히스테리시스로 안정화. Close=좁힘 / Hold=대기 / Space=벌림.</summary>
public enum SpacingIntent { Close, Hold, Space }

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
    public required TacticsProfile Profile { get; set; }   // set: 경기 중 전술 전환(감독 개입) 허용
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
    // [7]§6.2 미구현분 — 광란·빠른손·반격가·강심장·겁없는자
    public float TraitAtkSpeedMult = 1f;      // 광란: 공속(모션 시간 ÷)
    public float TraitDamageDealtMult = 1f;   // 광란의 대가: 주는 피해 배율
    public float TraitSkillCdMult = 1f;       // 빠른손: 액티브 CD 배율
    public float TraitCounterWindowAdd;       // 반격가: 카운터 창 가산
    public float TraitCounterDmgMult = 1f;    // 반격가: 카운터 피해 배율
    public float TraitNonCounterDmgMult = 1f; // 반격가의 대가: 비카운터 피해 배율
    public float TraitEmotionResist = 1f;     // 강심장: 감정 트리거 확률 배율
    public bool  TraitFearImmune;             // 겁없는자: 공포 면역

    // 무기 액티브([7]§4) — AI가 조건·확률로 자동 발동. 장착 없으면 전부 기본값 = 매트릭스 무영향.
    // 선천 부여로 액티브를 둘까지 지닐 수 있다(Ⅰ급·Ⅱ급). [7]§1 '동시 발동 1개'는 유지 —
    // 여러 개를 지니되 한 번에 하나만 발동하고, 쿨타임은 스킬마다 따로 흐른다.
    public List<Data.ActiveSpec> ActiveSkills = new();      // 보유한 액티브 전부
    public Dictionary<string, float> SkillReadyPer = new(); // ReasonTag → 다음 사용 가능 시각
    public Data.ActiveSpec? ActiveSkill;  // 마지막으로 발동한(=현재 버프/차지 주체) 액티브
    public float SkillBuffUntil = -1f;    // 발동 효과 만료 시각
    public float SkillAtkBonus;           // 광전사의 도끼: 발동 시점 스냅샷(+0.8%/부족HP%p, 캡 +40%)
    public float SkillFullBlockUntil;     // 방패 막기: 완전 차단 창
    public float SkillCounterBoostUntil;  // 방패 막기: 차단 직후 반격 보너스 창
    public float SkillAutoCounterUntil;   // 철벽 반격: 자세 창(최초 피격 1회 반격 후 소멸)
    public float SkillNextLightCritUntil; // 그림자 보: 다음 약공 확정 크리(미사용 시 소멸)
    public float SkillRootedUntil;        // 휘감기: 이동봉쇄
    public float SkillNextPokeAt;         // 공간 지배: 다음 자동 견제 가능 시각
    public float SkillExecStrikeAt = -1f; // 심판의 일격: 차지 완료(타격) 시각 — 차지 중 무방비
    public float LastStunAt = -99f;       // 마지막 경직(HitStun/Stagger) 진입 시각 — 난무의 '확정 히트 창' 근사

    // 성격 패시브([7]§5) — 조건 충족 시 자동 proc. 미장착이면 전부 기본값 = 매트릭스 무영향.
    public Data.PassiveSpec? Passive;     // 장착된 proc형 패시브(첫 하나만)
    public float PassiveReadyAt;          // proc 쿨다운
    public float PassiveBuffUntil = -1f;  // 시한 효과 만료
    public int   PassiveStacks;           // 투지·관중몰이 스택
    public float PassiveStackExpiry;      // 스택 만료(투지)
    public int   FearStacks;              // 공포 군림: 이 선수가 받는 공포 단계
    public float FearHpMark = 1f;         // 공포 군림: 마지막 공포 부여 시점의 HP 비율
    public float DodgeRefundPct;          // 생존 본능: 회피 성공 시 스태미나 환급 비율(창 열림 중)
    public float DodgeIFrameBonus;        // 생존 본능: 회피 무적 연장(창 열림 중)

    public bool Has(string traitId) => Traits.Contains(traitId);
    public float EffRange => Weapon.Range * RangeMult + RangeBonus;   // 유효 사거리 (거인: ×RangeMult 비례)

    // --- 위치 (B: 2D-lite. 원형 핏, 중심 0,0. 문서[8]) ---
    public Vec2 Pos;
    public Vec2 PrevPos;                // 이번 틱 이동 직전 위치 — disc 충돌이 "누가 파고들었나"를 가리는 데 사용
    public Vec2 Vel;                    // 현재 이동 속도(m/s) — 스티어링 관성. desired velocity로 가속제한 수렴 → 방향 순간이동 금지(무게감)
    public float CircleSign = 1f;       // 선회 방향 (+1 반시계 / -1 시계). 경계 도달 시 반전

    // --- 관중 기세 (문서[10]) — MatchSim이 군중게이지로부터 매 틱 갱신 ---
    public float CrowdMomentum;         // 0~1: 관중이 내 편(유리). 보편 기세 버프(공격성·커밋·회복·데미지·이속).
    public float CrowdPressure;         // 0~1: 관중이 상대 편(불리). 위축은 성격 민감도(CrowdSensitivity)에 비례.

    // --- 감정 (T10, 일시적 심리 상태) — 경기 시작 시 CreateRuntime이 EmotionIds로 세팅. decision-only(데미지 무관) ---
    public float EmotionTriggerProbMod; // 트리거 발동 확률 가산 (도발/격노 등이 더/덜 터짐)
    public readonly List<ParamMod> EmotionMods = new();  // Directive 결정 가중치 (RebuildDirective에서 합성)

    // --- 관계 (T11, 특정 상대를 향한 누적 관계) — CreateRuntime이 RelationToOpp로 세팅. decision-only + 트리거 게이트 ---
    public RelationType? Relation;       // 이 경기 상대에 대한 관계 (없으면 null). 트리거 플래그(OppIsNemesis 등) 결정
    public float RelationTriggerProbMod; // 트리거 발동 확률 가산
    public readonly List<ParamMod> RelationMods = new();  // Directive 결정 가중치 (그 상대 한정)

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
    public TacticSwitch[]? Switches;                       // 경기 중 전술 전환 예약(감독 개입) — 입력의 일부(결정론)
    public int SwitchIdx;                                  // 다음 적용할 전환 인덱스
    public readonly List<ActiveOverride> Overrides = new();
    public readonly Dictionary<string, float> CooldownUntil = new();
    public ActionRequest PendingForced = ActionRequest.None; // ForcedHeavy 인터럽트
    public float RepositionUntil;        // [안B] 공격 후 이탈 창 — 이 시각까지 후퇴 강제(카이터 찌르고 빠짐)
    public SpacingIntent Intent = SpacingIntent.Hold;  // 스페이싱 의도(안2) — 히스테리시스로 안정화된 거리 의사
    public float IntentSince;            // 마지막 의도 전환 시각 — dwell(최소 유지시간) 판정용
    public int NextDecisionTick;         // 판단주기 지터: 이 틱부터 다음 전술 판단 가능 (선수별 불규칙 반응 리듬)

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
    public int Blocks, Dodges;   // 기록실 계측: 방어(가드·패링) 성공·회피 성공 (전투 결과 무영향)
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
        // 감정(T10): 일시적 심리 상태 = 결정 가중치 상시 보정 (의사선택만, 데미지 무관). 성격 상시보정과 동급 층.
        foreach (var m in EmotionMods) Dir.Apply(m);
        // 관계(T11): 특정 상대를 향한 누적 관계도 결정 가중치 보정 (그 상대 한정, decision-only).
        foreach (var m in RelationMods) Dir.Apply(m);
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
