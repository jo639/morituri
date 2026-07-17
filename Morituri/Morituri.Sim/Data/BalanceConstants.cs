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
    public float DamageGlobalMult  { get; init; }  // 전역 데미지 배율 — TTK 단축 노브(M4-b 0.5× 관전: 적은 수의 무거운 공방). 전 무기 비율 보존

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

    // --- 출혈 (별도 트랙, 문서[7]§2) ---
    public float BleedDurationSec { get; init; }  // 적용/갱신 시 지속 시간
    public int   BleedMaxStacks   { get; init; }  // 합산 상한 (스택당 무기 BleedDps)

    // --- 방패 패링 (방패 전용, 문서[7] 방어형) ---
    public float ParryChance              { get; init; }  // 자격창 내 패링 성공 확률 — 성공률 다이얼(창의 계단 회피, 매끄러운 조절)
    public float ParryRefundStamina       { get; init; }  // 패링 성공 시 스태미나 환급(프레임 우위 자원)
    public int   ParryStunStacksMax        { get; init; }  // 패링당함 누적 → 공격자 기절 임계
    public float ParryStunDecaySec         { get; init; }  // 누적 스택 1개 감쇠 주기
    public float ShieldGuardBreakStaggerSec { get; init; } // 방패 가드붕괴 완화 스태거(기존 1.2 대비 짧게)

    // --- 흡수 쉴드 (선취점 특성·향후 방패 액티브, 문서[7]) ---
    public float FirstBloodShield    { get; init; }  // 선취점 부여 흡수량
    public float FirstBloodShieldSec { get; init; }  // 선취점 쉴드 지속

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
    public float KiteCostMinRange    { get; init; }  // 카이팅 비용 적용 최소 사거리 — 이 이상 무기만 과금. 3.0=장사거리전용 / 0=전무기(튜닝 스윕 차원)
    public float KiteBrakeStamFrac   { get; init; }  // 이 스태미나 비율 미만이면 장사거리 카이터가 무한 후퇴 대신 Hold(회복)로 전환 — 자멸 카이팅 방지. 0=끔
    public float KiteBrakeReachSpan  { get; init; }  // 브레이크 리치 감쇠폭: (사거리−KiteCostMinRange)이 이만큼이면 브레이크 0. 대검엔 적용·창/채찍은 세금 보존
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

    // --- 이동 스티어링(안1, 연출 노브 — MoveReactDelaySec과 동급, 밸런스 무손상) ---
    // 결정층(Approach/Retreat/Strafe 라벨)은 그대로. 라벨→위치 번역만 매끄럽게: 목표거리 근처 감속(arrive)으로
    // 오버슈트·튕김 제거, 접선 혼합(orbit)으로 직각 스텝 대신 호, 가속제한(maxAccel)으로 방향 순간이동 제거.
    // 히트/충돌은 실시간 Pos로 판정하므로 매트릭스 무관(거울 KO만 점검).
    public float SteerArriveBand   { get; init; }  // 목표거리 ±이 폭에서 방사속도 0→full 선형 램프(감속 밴드, m)
    public float SteerOrbitGain    { get; init; }  // 접선(선회) 혼합 비율 — 호의 곡률(0=직선)
    public float SteerMaxAccel     { get; init; }  // 속도 변경 상한(m/s²) — 작을수록 무겁고 굼뜸

    // --- 스페이싱 의도 히스테리시스(안2) — 거리 댄스(경계 깜빡임)를 commit으로, 무의미한 선회를 Hold(대기)로 ---
    public float SpacingDwellSec        { get; init; }  // 의도 전환 최소 간격(초) — 잔떨림 억제(commit 강제)
    public float SpacingHoldReleaseRatio{ get; init; }  // Close/Space → Hold 해제 임계 = band × 이값 (이중임계 = 히스테리시스)

    // --- 판단주기 지터(1단계) — 전술 판단 간격을 매번 [Min,Max]×DecisionTickSec로 추첨(불규칙 반응 리듬) ---
    public float DecisionJitterMinMult  { get; init; }  // 최소 배율 (중앙 1.0 유지 시 평균 반응지연 보존)
    public float DecisionJitterMaxMult  { get; init; }  // 최대 배율
    public float TauntProbMult          { get; init; }  // 도발 인터럽트 확률 전역 보정 — 지터가 증폭한 도발 메타 재정렬(1.0=중립)

    // --- 인내심 (영원 대치 해소): 무교전이 길어지면 소모 → 공격 충동. 거울전(reachAdvantage 부재) 교착 방지 ---
    public float PatienceMax           { get; init; }  // 가득 찬 인내심
    public float PatienceDrainBase     { get; init; }  // 초당 감소 기준 ×(0.5+Aggression) — 공격적일수록 빨리 소진(전술/성격/특성 반영)
    public float PatienceImpulseScale  { get; init; }  // 인내심 0일 때 공격 점수 가산 배수
    // 안 A(수비형 짝 180초 동결 해소): 근접 무기는 평소 교전이 잦아 충동 게이트에서 제외되나,
    // '쌍방 장기 무교전'(마지막 클린히트 이후 경과 = min NoHitTimer)이 유예를 넘기면 근접에도 충동 개방.
    // 정상 근접전은 유예를 못 넘겨 무영향 → 검 거울(매트릭스 대조군) 불변.
    public float StalemateGraceSec     { get; init; }  // 이 시간까지의 무교전은 정상 수싸움 — 충동 억제
    public float StalemateRampSec      { get; init; }  // 유예 후 이 폭에 걸쳐 근접 충동 0→1 램프
    public float UtilityNoise      { get; init; }  // ε = 0.10 — 이변의 원천 1
    public float AttackGateScale   { get; init; }  // Commit 게이트: 공격 채택 요구 점수 = Commit × 이 값
    public float CancelWindowRatio { get; init; }  // 선딜 중 캔슬 가능 비율 0.7
    public float DodgeDurationSec  { get; init; }
    public float DodgeIFrameSec    { get; init; }
    public float DodgeDistance     { get; init; }
    public float DownDurationSec   { get; init; }
    public float GetUpDurationSec  { get; init; }
    public float TauntDurationSec  { get; init; }
    public float TauntRageAggression { get; init; }  // 도발당한 상대 분노: Aggression +이값 (성격이 가감)
    public float TauntRageCommitAdd  { get; init; }  // 도발당한 상대: CommitThreshold +이값 (보통 음수 → 더 잘 지름)
    public float TauntRageDurationSec{ get; init; }  // 분노 지속. > TauntDurationSec여야 도발 후 카운터 창이 생김
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
        DamageGlobalMult = 1.0f,  // M4-b: 전역 데미지 배율 레버(전 무기 비율 보존). 1.0=중립. 루즈함의 원인은 데미지가
                                  // 아니라 경기 절반을 차지하는 비전투 이동(뷰어 동적 재생속도로 압축) → 1.0 유지. 무겁게=doc[9].

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

        BleedDurationSec = 4f,   // 출혈 지속(초안 — sigmatrix 튜닝)
        BleedMaxStacks = 3,

        ParryChance = 0.4f,                // 자격창 내 패링 성공률(초안 — 방패 균형 다이얼)
        ParryRefundStamina = 12f,          // 패링 환급(약공 1.5회분)
        ParryStunStacksMax = 3,            // 3회 패링당하면 기절
        ParryStunDecaySec = 3f,            // 3초마다 1스택 감쇠 (연속 패링 아니면 안 쌓임)
        ShieldGuardBreakStaggerSec = 0.5f, // 방패 붕괴 완화(1.2→0.5): 파국적 0% 모드 축소

        FirstBloodShield = 50f,            // 선취점 흡수량(약 1히트분)
        FirstBloodShieldSec = 8f,          // 선취점 쉴드 지속

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
        KiteCostMinRange = 3.0f,      // 기본=장사거리(MinLongRange)만 카이팅 과금 (기존 동작 보존). 튜닝 스윕이 낮춰 전무기로 확장
        KiteBrakeStamFrac = 0.35f,    // 스태미나 35% 미만 = 가스아웃 임박 → 후퇴/선회 대신 Hold(회복+6, 세금0)
        KiteBrakeReachSpan = 1.2f,    // 리치 스케일: 사거리가 KiteCostMinRange보다 이만큼 길면 브레이크 0 (대검=full·창≈0.25·채찍=0 — 장리치 카이팅 세금 보존)
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
        SteerArriveBand = 0.4f,   // 교전거리 0.4m 안에서만 감속 — 그 전엔 풀스피드로 붙어 교전 빈도 보존(KO율 유지)
        SteerOrbitGain = 0.35f,   // 선회(Strafe) 접선 비율 — 카이터 호의 곡률
        SteerMaxAccel = 10f,      // 이속 ~2.5m/s를 0.25s에 도달 — 무게감 있으나 굼뜨지 않게
        SpacingDwellSec = 0.35f,        // 의도 전환 후 0.35s 유지 — 0.2s 결정틱마다 뒤집히던 잔떨림 제거
        SpacingHoldReleaseRatio = 0.4f, // 교전거리 ±(band×0.4)까지 돌아와야 Hold 해제 → 진입 band, 이탈 0.4band (히스테리시스)
        DecisionJitterMinMult = 0.4f,   // 0.2s×0.4=0.08s ~ ×1.6=0.32s, 중앙 1.0 → 평균 0.2s 보존(밸런스 격리)
        DecisionJitterMaxMult = 1.6f,
        TauntProbMult = 0.2f,           // 지터 도발 증폭 보정(수렴 중) — 거울 도발률·챔피언전을 baseline으로
        PatienceMax = 100f,
        PatienceDrainBase = 10f,      // 인내형(Agg 0.2)≈14초·공격형(Agg 0.8)≈8초 무교전이면 충동 최대
        PatienceImpulseScale = 2.0f,  // 인내심 0 → 공격 점수 ×3 (reachAdvantage ×2.2 수준의 결단)
        StalemateGraceSec = 12f,      // 정상 근접전은 12초 무교전에 도달 안 함 → 대조군 보존
        StalemateRampSec = 8f,        // 12→20초 교착서 근접 충동 0→1 (수비형 짝 ~20초 내 개전)
        UtilityNoise = 0.10f,
        AttackGateScale = 0.9f,   // 1.6은 카운터형이 영원히 공격 못 하는 값이었음 (M2 디버깅으로 발견)
        CancelWindowRatio = 0.7f,
        DodgeDurationSec = 0.40f,
        DodgeIFrameSec = 0.30f,
        DodgeDistance = 1.2f,
        DownDurationSec = 1.5f,
        GetUpDurationSec = 0.7f,
        TauntDurationSec = 1.5f,
        TauntRageAggression = 0.25f,
        TauntRageCommitAdd = -0.10f,
        TauntRageDurationSec = 5.0f,
        FeintCancelRatio = 0.5f,
        FeintRecoverySec = 0.25f,
        ArenaRadius = 12f,  // 지름 24m — 넓은 핏(외곽 선회·도주 공간↑). 뷰어가 R()로 정규화해 이동이 더 느려 보임. (밸런스: 카이터 여유↑)
        StartGap = 20f,   // 거의 양쪽 끝에서 시작(±10, 반지름 12 아레나) — 코너 페널티존(>10.5) 바로 안쪽. 밸런스 무영향(buildmatrix A/B Δ<1%p)
        CornerZone = 1.5f,
        InnerRangeRatio = 0.4f,
        MinLongRange = 3.0f,   // 거리 ×1.5 스케일 동반(검 2.4 근접 유지, 대검3.0·창3.9·채찍4.5 장사거리)
        CollisionRadius = 0.6f,  // 반경합 1.2m — 거리 ×1.5 스케일. 쌍검(1.65)도 적중 가능(1.2<1.65)
    };
}
