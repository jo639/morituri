namespace Morituri.Sim.Data;

/// <summary>
/// T06_BalanceConstants (문서[5] 7장).
/// 문서[4]에 흩어진 모든 전역 계수의 단일 출처. 코드에는 매직 넘버를 두지 않는다.
/// M1에서는 Default 정적 인스턴스를 쓰고, M3에서 CSV → 이 구조체 로딩으로 교체한다.
/// readonly struct: 런타임 불변 (Sim POD 원칙).
/// </summary>
public readonly record struct BalanceConstants
{
    // --- 데미지 공식 (문서[4] 2장) ---
    public float DefCurve       { get; init; }  // 승산곡선 계수
    public float CritMult       { get; init; }
    public float CounterMult    { get; init; }
    public float GuardDmgMult   { get; init; }  // 가드 성공 시 데미지 배율
    public float VarianceMin    { get; init; }  // U(min, max)
    public float VarianceMax    { get; init; }
    public float CritBase       { get; init; }  // Crit% = base + (ATK-DEF)*scale
    public float CritScale      { get; init; }
    public float CritMinPct     { get; init; }
    public float CritMaxPct     { get; init; }
    public float InnerRangePenalty { get; init; }  // 사거리 안쪽 침투 피해 배율 (문서[4] 8장)

    // --- 가드 (문서[4] 4장) ---
    public float GuardStaminaCostRatio { get; init; }  // RawDamage × 이 값만큼 스태미나 소모
    public float GuardedRecoveryMult   { get; init; }  // 가드시킨 공격의 후딜 배율 (프레임 불리 — 방어자의 턴)
    public float GuardBreakStaggerSec  { get; init; }
    public float GuardGaugeRecoverPctPerSec { get; init; }  // 비가드 상태 초당 Max 대비 회복률
    public float GuardBreakRecoverToPct     { get; init; }  // 게이지 붕괴 후 재사용 가능 회복선

    // --- 경직 (문서[4] 5장) ---
    public float StaggerSec          { get; init; }
    public float HitStunBase         { get; init; }  // HitStun = base + PoiseDmg*scale
    public float HitStunPerPoiseDmg  { get; init; }
    public float PoiseRecoverPctPerSec { get; init; }
    public float PoiseRecoverDelaySec  { get; init; }  // 피격 후 회복 정지

    // --- 스태미나 (문서[4] 6장) ---
    public float StamCostAttackLight { get; init; }
    public float StamCostAttackHeavy { get; init; }
    public float StamCostWhiff       { get; init; }  // 헛스윙 추가 소모 (허공 가르기 = 과도한 커밋). 난전형 가스아웃 유발
    public float StamCostDodge       { get; init; }
    public float StamCostGuardPerSec { get; init; }
    public float StamCostSprintPerSec{ get; init; }
    public float StamRegenIdle       { get; init; }
    public float StamRegenMoving     { get; init; }
    public float KiteStamCostPerSec  { get; init; }  // 존형이 거리 유지(후퇴/선회)로 빠질 때 소모 (B: 카이팅 비용)
    public float ExhaustDurationSec  { get; init; }
    public float ExhaustMoveSpeedMult{ get; init; }
    public float ExhaustDamageTakenMult { get; init; }  // 지친 방어자가 받는 피해 배수 (무너진 몸 = 처벌 강화)
    public float ExhaustPoiseDmgTakenMult { get; init; }

    // --- 파생 스탯 (문서[4] 1장) ---
    public float StaminaMaxBase   { get; init; }  // 60 + HP*0.05
    public float StaminaMaxPerHp  { get; init; }
    public float GuardGaugeBase   { get; init; }  // 40 + DEF*0.4
    public float GuardGaugePerDef { get; init; }
    public float MoveSpeedBase    { get; init; }  // m/s = 2.0 + SPD*0.02
    public float MoveSpeedPerSpd  { get; init; }
    public float AspdMotionBase   { get; init; }  // 모션시간 = 기본 / (모션속도 * (base + ASPD/div))
    public float AspdMotionDiv    { get; init; }

    // --- 모션 (문서[4] 7장) ---
    public float MotionMultLight { get; init; }
    public float MotionMultHeavy { get; init; }

    // --- 경기/판정 (문서[4] 10장) ---
    public float MatchTimeSec        { get; init; }
    public float ScorePerDamage      { get; init; }
    public float ScorePerCleanHit    { get; init; }
    public float ScorePerKnockdown   { get; init; }
    public float ScorePerAttackAttempt { get; init; }
    public float ScorePenaltyPerCornerSec { get; init; }

    // --- AI/FSM/경기 진행 (문서[3], M2) ---
    public float DecisionTickSec   { get; init; }  // 전술층 주기 0.2s
    public float StrategyTickSec   { get; init; }  // 전략층 주기 1s
    public float UtilityNoise      { get; init; }  // ε = 0.10 — 이변의 원천 1
    public float AttackGateScale   { get; init; }  // Commit 게이트: 공격 채택 요구 점수 = Commit × 이 값
    public float CancelWindowRatio { get; init; }  // 선딜 중 캔슬 가능 비율 0.7
    public float DodgeDurationSec  { get; init; }
    public float DodgeIFrameSec    { get; init; }
    public float DodgeDistance     { get; init; }
    public float DownDurationSec   { get; init; }
    public float GetUpDurationSec  { get; init; }
    public float TauntDurationSec  { get; init; }
    public float FeintCancelRatio  { get; init; }  // 페인트: 선딜 × 이 비율에서 중단
    public float FeintRecoverySec  { get; init; }
    public float ArenaRadius       { get; init; }  // 원형 핏 반지름 (중심 0,0). B: 2D-lite (문서[8])
    public float StartGap          { get; init; }  // 시작 시 두 선수 간 거리
    public float CornerZone        { get; init; }  // 경계에서 이 거리 이내 = 가장자리 (판정 패널티)
    public float InnerRangeRatio   { get; init; }  // dist < range×비율 → 안쪽 침투 판정
    public float MinLongRange      { get; init; }  // 이 사거리 이상 무기만 침투 패널티 대상
    public float CollisionRadius   { get; init; }  // 캐릭터 원형 점유 반경(m). 두 disc(2×r)는 겹칠 수 없다 — 통과·위치교환 금지

    /// <summary>문서[4] v0.1 초기값. 모든 수치는 M3 배치 시뮬레이션으로 튜닝 대상.</summary>
    public static readonly BalanceConstants Default = new()
    {
        DefCurve = 0.8f,
        CritMult = 1.6f,
        CounterMult = 1.35f,
        GuardDmgMult = 0.25f,
        VarianceMin = 0.92f,
        VarianceMax = 1.08f,
        CritBase = 5f,
        CritScale = 0.05f,   // TODO(M3): 상한 20%가 도달 불가능한 죽은 값. 계수↑ 또는 상한↓ 튜닝 필요
        CritMinPct = 2f,
        CritMaxPct = 20f,
        InnerRangePenalty = 0.6f,

        // M3-A 개정 0.15→0.06: 가드는 공격자보다 경제적이어야 한다. 0.15에서는 블록당 드레인+유지비가
        // 공격자 스윙 비용과 비등 + 칩딜까지 맞아 "가드 = 천천히 지는 행동"이었고, 방어형이 전 매치업 0%였다.
        // 가드가 버티면 광공격자가 먼저 지치고 → 지침 처벌 창(×2.2)이 방어형의 승리 플랜이 된다.
        GuardStaminaCostRatio = 0.06f,
        // M3-A 신규: 가드시킨 공격은 후딜 ×1.8 (막힌 칼이 튕겨나옴 = 프레임 불리).
        // 이게 없으면 약공(후딜 0.36s)은 가드에 완전 안전 — 처벌 응답시간이 인지지연 0.16 +
        // 의사결정 틱 평균 0.1 + 선딜 0.31 ≈ 0.57s라 "약공 스팸"이 무손실 지배 전략이 된다.
        // 검 약공 가드 시 0.36×1.65=0.59s ≈ 0.57s → 처벌이 '확률적'으로 성립 (틱 정렬 운에 따라 절반쯤).
        // 1.8(확정 처벌)은 공격 행위 자체를 자살로 만들어 수비 과잉 지배 메타가 됐다 (매트릭스 검증).
        GuardedRecoveryMult = 1.65f,
        GuardBreakStaggerSec = 1.2f,
        GuardGaugeRecoverPctPerSec = 0.06f,
        GuardBreakRecoverToPct = 0.5f,

        StaggerSec = 0.8f,
        HitStunBase = 0.15f,
        HitStunPerPoiseDmg = 0.004f,
        PoiseRecoverPctPerSec = 0.10f,
        PoiseRecoverDelaySec = 1.0f,

        StamCostAttackLight = 8f,
        StamCostAttackHeavy = 18f,
        StamCostWhiff = 12f, // M3-A: 16→12 — 가드 프레임불리·헛스윙처벌 회피게이트 등 신규 처벌 경로가
                             // 추가되며 16은 과잉(버서커:전술가 10:90). 가스아웃 빈도만 낮춰 재수렴.
        StamCostDodge = 15f,
        StamCostGuardPerSec = 0.5f, // M3-A: 2→0.5 — 자세 유지는 싸고 막는 행위(블록 드레인)가 비용.
                                    // 2/s에서는 수비형이 가드 중 Reserve까지 말라 처벌 예산이 굶었다.
        StamCostSprintPerSec = 4f,
        StamRegenIdle = 6f,
        StamRegenMoving = 3f,
        KiteStamCostPerSec = 1.5f,   // B 재튜닝: 카이팅 비용 노브 (0→창78% / 1.5→48% / 5→0%). 정확값은 트위치, 1.5 = 근접 균형
        ExhaustDurationSec = 3.0f,   // M3-A: 문서[4] 원값으로 복귀 (아래 주석)
        ExhaustMoveSpeedMult = 0.6f,
        // M3-A: 2.2→1.3 — 지침 중처벌(4.5s/×2.2)은 가드 프레임불리·헛스윙 처벌이 없던 시절의
        // 유일한 처벌 수단이었다. 신규 메커니즘과 중첩되자 저(低)리저브 전술이 연쇄 가스아웃
        // 사형 루프에 빠짐(버서커 8회 연속 지침, 매트릭스 난전 행 전패). 보조 역할로 격하.
        ExhaustDamageTakenMult = 1.3f,
        ExhaustPoiseDmgTakenMult = 1.5f,

        StaminaMaxBase = 60f,
        StaminaMaxPerHp = 0.05f,
        GuardGaugeBase = 40f,
        GuardGaugePerDef = 0.4f,
        MoveSpeedBase = 2.0f,
        MoveSpeedPerSpd = 0.02f,
        AspdMotionBase = 0.7f,
        AspdMotionDiv = 250f,

        MotionMultLight = 0.7f,
        MotionMultHeavy = 1.5f,

        MatchTimeSec = 180f,
        ScorePerDamage = 1.0f,
        ScorePerCleanHit = 8f,
        ScorePerKnockdown = 40f,
        ScorePerAttackAttempt = 1.5f,
        ScorePenaltyPerCornerSec = 2f,

        DecisionTickSec = 0.2f,
        StrategyTickSec = 1.0f,
        UtilityNoise = 0.10f,
        AttackGateScale = 0.9f,   // 1.6은 카운터형이 영원히 공격 못 하는 값이었음 (M2 디버깅으로 발견)
        CancelWindowRatio = 0.7f,
        DodgeDurationSec = 0.40f,
        DodgeIFrameSec = 0.30f,
        DodgeDistance = 1.2f,
        DownDurationSec = 1.5f,
        GetUpDurationSec = 0.7f,
        TauntDurationSec = 1.5f,
        FeintCancelRatio = 0.5f,
        FeintRecoverySec = 0.25f,
        ArenaRadius = 8f,   // 지름 16m = 옛 1D 폭과 동일 스케일, 단 원형이라 선회 공간 존재
        StartGap = 4f,
        CornerZone = 1.5f,
        InnerRangeRatio = 0.4f,
        MinLongRange = 2.0f,
        CollisionRadius = 0.4f,  // 반경합 0.8m — 두 캐릭터 점유 공간 (Disc 충돌, 통과·교환 금지)
    };
}
