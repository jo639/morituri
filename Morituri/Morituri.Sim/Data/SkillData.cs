namespace Morituri.Sim.Data;

/// <summary>
/// T12 패시브 스킬 MVP (문서[6]§3.1 "엔진은 하나, 경제는 둘" · [7]§5).
/// 스킬 = <b>장착형 특성 행</b> — TraitDef를 그대로 재사용해 Sim 훅 추가가 없다(원칙 A/C).
/// 특성(T09)과의 차이는 경제뿐: 특성=타고남·탈부착 불가 / 스킬=성격·천부 게이트 안에서 습득·교체(Game 층 슬롯).
/// 생성 추첨 풀(TraitGen)에는 절대 들어가지 않는다 → 미장착 세계의 매트릭스는 구조적으로 불변.
/// 수치 원칙: 타고난 특성보다 항상 약하게(스킬은 보완재, [6]§3.2 "낮은 계급에도 유용한 것").
/// </summary>
/// <summary>액티브 발동 조건([7]§4 트리의 조건 게이트) — 상태 기반, AI가 스스로 판단.</summary>
public enum SkillTrigger
{
    SelfHpBelow,          // 자기 HP 비율 ≤ 임계 (광전사의 도끼)
    EvenFight,            // 호각 — HP 격차 ≤ 임계%p & 교전 지속 (결투의 격)
    ConsecHitsTaken,      // 연속 피격 ≥ 임계 (불퇴의 자세)
    OppGuarding,          // 상대 가드 중 & 사거리내 (분쇄 일격)
    OppGuardGaugeBelow,   // 사거리내 & 상대 가드게이지 비율 < 임계 (연격)
    GapBand,              // 간격이 [GapMinM, GapMaxM] 안 (견제 찌르기·쇄도 베기·휘감기·공간 지배)
    OppHeavyWindupOrPress,// 상대 강공 선딜 인지 or 근접 압박 (철벽 반격)
    OppHeavyWindupOrRecovery, // 상대 강공 선딜 or 후딜 (그림자 보)
    OppVulnerable,        // 상대 경직/가드붕괴/스태거 — 확정 히트 창 (난무)
    InRange,              // 사거리내 (대지 강타)
    OppExecutable,        // 상대 HP < 임계 or 다운/스태거 (심판의 일격 — 거부권 대상)
    OppWindupAny,         // 상대 공격 선딜 인지 — 반응형 최우선 (방패 막기)
    OppGuardingOrStunned, // 상대 가드 중/경직 (방패 밀치기)
}

/// <summary>효과 형태 — 전용 모션 없이 기존 프리미티브로 구현(즉발 타격·위치 이동·시한 플래그). 연출은 애니메이션 트랙에서.</summary>
public enum ActiveKind { Buff, Strike, Stance, Charge }

/// <summary>
/// [7] 무기 액티브 명세 — <b>AI가 조건·확률로 발동</b>한다(관전형 — 감독이 누르는 게 아님, [7] 전제).
/// 발동 = [7]§1 트리(쿨→상태→코스트→거부권→조건→타당성→확률 롤) 통과.
/// 코스트: ST(공격 버스트)/HP%(배수진)/GG(방어)/CD만(수비·유틸) — [7]§0.
/// ⚠ 공간 수치(간격·이동거리)는 [7]의 ×1.5 스케일 이전 값을 현행 스케일로 환산해 담는다.
/// </summary>
public sealed record ActiveSpec(
    string ReasonTag,                       // [7] 가시화 원칙 — Decision("SKILL_"+tag)로 발동 방출
    SkillTrigger Trigger, float Threshold, float Prob,
    float Duration, float CooldownSec,
    ActiveKind Kind = ActiveKind.Buff,
    float StCost = 0f, float SelfHpPctCost = 0f, float GgCost = 0f,
    float GapMinM = 0f, float GapMaxM = 0f,     // GapBand 트리거용(현행 스케일)
    // ── Buff 효과 ──
    float CounterWindowAdd = 0f,            // 결투의 격: 카운터창 +0.3 (Override 파이프, 캡 +0.6 [7]§2)
    float DmgTakenMult = 1f,                // 광전사의 도끼: 받피 +25% (설계 의도된 리스크)
    float AtkPerMissingHpPct = 0f, float AtkCap = 0f,   // 광전사: 공격력 +0.8%/(부족 HP%p), 최대 +40%
    bool PoiseImmune = false,               // 불퇴의 자세: 포이즈 무한 = 스태거/넉백 면역(가드붕괴·다운은 아님)
    bool SunderNextHeavy = false,           // 분쇄 일격: 다음 강공 가드 무조건파괴 1회(미사용 시 만료 소멸)
    float AttackSpeedMult = 1f,             // 연격: 공속 +35% (모션 시간 ÷) — 광폭화와 가산 캡은 모션 트랙에서
    float EarlyEndGapM = 0f,                // 연격: 상대가 이 거리보다 멀어지면 조기 종료
    bool KiteExempt = false,                // 공간 지배: 카이팅 ST 소모 면제
    float AutoPokeMult = 0f, float AutoPokeIntervalSec = 0f,  // 공간 지배: 사거리 진입자 자동 견제(약공 ×0.6 / 0.8s)
    // ── Strike 효과(즉발 — 모션 없는 1차 구현) ──
    bool StrikeHeavy = false, float StrikeDmgMult = 1f, int StrikeHits = 1,
    float KnockbackM = 0f,                  // 견제 찌르기: 넉백(하이퍼아머·불퇴면 무효, 피해는 적용)
    float PullM = 0f, float RootSec = 0f,   // 휘감기: 끌어당김/이동봉쇄 택1(거리 따라)
    bool DashIn = false,                    // 쇄도 베기·방패 밀치기: 상대에게 돌진 후 타격
    float StaggerOnHitSec = 0f, float GuardPierce = 0f,  // 대지 강타: 명중 시 스태거·가드관통 50%
    bool BashBreak = false, float DownSec = 0f,          // 방패 밀치기: 가드붕괴+다운(면역이면 붕괴만)
    bool TeleportBehind = false, float NextLightCritSec = 0f,  // 그림자 보: 배후 이동+다음 약공 확정크리
    // ── Stance 효과(피격 반응) ──
    bool FullBlock = false, float CounterBoostMult = 1f, float CounterBoostSec = 0f, // 방패 막기: 완전차단+직후 반격 보너스
    bool AutoCounter = false,               // 철벽 반격: 자세 중 최초 피격 1회에 즉시 반격
    // ── Charge 효과(심판의 일격) ──
    float ChargeSec = 0f, float ExecuteDmgMult = 0f, float ExecuteKillPct = 0f,
    bool VetoExecution = false);            // 거부권 대상([7]§8 — 고결은 처형류 발동 거부

public sealed record SkillDef(
    TraitDef Def,               // 효과 본체(패시브) 또는 식별자(액티브 — 배율 전부 1)
    string GatePersonality,     // 패시브 = 성격 결합([7]§5). 액티브는 무기 게이트(GateWeapon)라 빈 문자열
    int RankTier,               // Ⅰ=1(전 천부) / Ⅱ=2(집정관 이상 — [6]§1.5 접근권)
    string Desc,
    ActiveSpec? Active = null,  // null=패시브
    string? GateWeapon = null); // 액티브 = 무기 결합([7]§4 "무기별 액티브 2개")

public static class SkillTable
{
    /// <summary>Ⅱ급 접근에 필요한 최소 천부(집정관). Game 층이 TalentGrade와 비교.</summary>
    public const int Tier2MinTalent = 3;   // TalentGrade.Consul

    public static readonly SkillDef[] All =
    {
        // ── Ⅰ급 (전 천부) — 성격당 1종, 타고난 특성의 절반 이하 강도 ──
        new(new TraitDef("SKL_READ",    "간파(스킬)",    PerceptDelayAdd: -0.04f),                       "PER_CALM",        1, "상대의 예비동작을 읽는다 — 인지지연 −0.04s"),
        new(new TraitDef("SKL_RUSH",    "저돌(스킬)",    MoveSpeedMult: 1.06f),                          "PER_RECKLESS",    1, "생각보다 몸이 먼저 — 이동속도 +6%"),
        new(new TraitDef("SKL_LEISURE", "여유(스킬)",    StamRegenMult: 1.15f),                          "PER_ARROGANT",    1, "서두르지 않는 자의 호흡 — 스태미나 회복 +15%"),
        new(new TraitDef("SKL_AEGIS",   "수호자(스킬)",  GuardDamageMult: 0.80f),                        "PER_HONORABLE",   1, "정면으로 받아낸다 — 가드 시 받는 피해 −20%"),
        new(new TraitDef("SKL_SURVIVE", "생존술(스킬)",  DamageTakenMult: 0.96f, DodgeCostMult: 0.85f),  "PER_COWARD",      1, "맞지 않는 것이 이기는 것 — 받피 −4%·회피 소모 −15%"),
        new(new TraitDef("SKL_VIGOR",   "살육의 활력(스킬)", StaminaMaxMult: 1.10f),                     "PER_CRUEL",       1, "피 냄새가 힘이 된다 — 스태미나 최대 +10%"),
        new(new TraitDef("SKL_NERVE",   "배짱(스킬)",    PoiseMaxMult: 1.12f),                           "PER_BOLD",        1, "물러서지 않는 심장 — 포이즈 +12%"),
        new(new TraitDef("SKL_ECONOMY", "절제(스킬)",    DodgeCostMult: 0.70f),                          "PER_WARY",        1, "낭비 없는 몸놀림 — 회피 스태미나 소모 −30%"),
        new(new TraitDef("SKL_FLAIR",   "무대 장악(스킬)", MoveSpeedMult: 1.04f, RangeAdd: 0.08f),       "PER_SHOWMAN",     1, "관중이 보는 각도를 안다 — 이속 +4%·간격 +0.08"),
        new(new TraitDef("SKL_ANGLE",   "노림수(스킬)",  PerceptDelayAdd: -0.03f, MoveSpeedMult: 1.03f), "PER_OPPORTUNIST", 1, "허점이 열리는 순간을 기다린다 — 인지 −0.03s·이속 +3%"),
        // ── Ⅱ급 (집정관 이상) — 더 강하지만 접근권이 좁다 ──
        new(new TraitDef("SKL_READ2",   "전장 분석(스킬)", PerceptDelayAdd: -0.08f),                     "PER_CALM",        2, "전장 전체가 느리게 보인다 — 인지지연 −0.08s"),
        new(new TraitDef("SKL_BULWARK", "불괴(스킬)",    GuardDamageMult: 0.65f, MoveSpeedMult: 0.96f),  "PER_HONORABLE",   2, "무너지지 않는 방벽 — 가드 받피 −35%·이속 −4%"),

        // ── 무기 액티브([7]§4 카탈로그, 8무기 × 2) — 전용 모션 없이 기존 프리미티브로 전량 구현.
        //    코스트/CD/확률/트리 = 문서 수치. 공간 수치는 ×1.5 현행 스케일 환산. 전용 연출은 애니메이션 트랙에서.
        // 검
        new(new TraitDef("SKL_COMBO", "연격(스킬)"), "", 1,
            "베기가 베기를 부른다 — 3.5초간 공격 속도 +35%, 상대가 멀어지면 조기 종료 (ST20 / 9s · 主ATK)",
            new ActiveSpec("COMBO", SkillTrigger.OppGuardGaugeBelow, 0.70f, 0.5f, 3.5f, 9f,
                StCost: 20f, AttackSpeedMult: 1.35f, EarlyEndGapM: 3.0f), GateWeapon: "WPN_SWORD"),
        new(new TraitDef("SKL_DUELIST", "결투의 격(스킬)"), "", 2,
            "호각의 상대와 격이 오른다 — 6초간 카운터 창 +0.3 (CD만 / 24s · 主RCT)",
            new ActiveSpec("DUELIST", SkillTrigger.EvenFight, 0.10f, 0.6f, 6f, 24f,
                CounterWindowAdd: 0.3f), GateWeapon: "WPN_SWORD"),
        // 창 — 카이터 복원 핵심([7])
        new(new TraitDef("SKL_LUNGE", "견제 찌르기(스킬)"), "", 1,
            "다가오는 걸음을 창끝이 벌한다 — 즉발 찌르기 + 넉백 (ST18 / 8s · 主SPD)",
            new ActiveSpec("LUNGE", SkillTrigger.GapBand, 0f, 0.6f, 0f, 8f, ActiveKind.Strike,
                StCost: 18f, GapMinM: 2.4f, GapMaxM: 4.2f, KnockbackM: 1.2f), GateWeapon: "WPN_SPEAR"),
        new(new TraitDef("SKL_RIPOSTE", "철벽 반격(스킬)"), "", 2,
            "받아치는 창 — 1.5초 자세, 그동안 최초 피격에 즉시 반격 (CD만 / 22s · 主RCT)",
            new ActiveSpec("RIPOSTE", SkillTrigger.OppHeavyWindupOrPress, 2.25f, 0.7f, 1.5f, 22f, ActiveKind.Stance,
                AutoCounter: true), GateWeapon: "WPN_SPEAR"),
        // 도끼
        new(new TraitDef("SKL_SUNDER", "분쇄 일격(스킬)"), "", 1,
            "가드째 부순다 — 다음 강공이 가드를 무조건 파괴 + 출혈 (ST22 / 11s · 主ATK, 5초 내 미사용 시 소멸)",
            new ActiveSpec("SUNDER", SkillTrigger.OppGuarding, 0f, 0.6f, 5f, 11f,
                StCost: 22f, SunderNextHeavy: true), GateWeapon: "WPN_AXE"),
        new(new TraitDef("SKL_BERSERK", "광전사의 도끼(스킬)"), "", 2,
            "제 피를 값으로 치른다 — HP 5% 자해, 8초간 공격력 +0.8%/(부족 HP%p) 최대 +40%·받는 피해 +25% (26s · 主ATK)",
            new ActiveSpec("BERSERK", SkillTrigger.SelfHpBelow, 0.50f, 0.7f, 8f, 26f,
                SelfHpPctCost: 0.05f, DmgTakenMult: 1.25f, AtkPerMissingHpPct: 0.008f, AtkCap: 0.40f), GateWeapon: "WPN_AXE"),
        // 대검
        new(new TraitDef("SKL_CHARGE", "쇄도 베기(스킬)"), "", 1,
            "거리를 지우는 돌진 — 상대에게 짓쳐들어 강공 일격 (ST22 / 10s · 主ATK)",
            new ActiveSpec("CHARGE", SkillTrigger.GapBand, 0f, 0.55f, 0f, 10f, ActiveKind.Strike,
                StCost: 22f, GapMinM: 3.0f, GapMaxM: 6.0f, DashIn: true, StrikeHeavy: true), GateWeapon: "WPN_GREATSWORD"),
        new(new TraitDef("SKL_UNBROKEN", "불퇴의 자세(스킬)"), "", 2,
            "물러서지 않는다 — 5초간 포이즈 무한(스태거·넉백 면역, 가드붕괴·다운은 아님) (CD만 / 24s · 主DEF)",
            new ActiveSpec("UNBROKEN", SkillTrigger.ConsecHitsTaken, 2f, 0.6f, 5f, 24f,
                PoiseImmune: true), GateWeapon: "WPN_GREATSWORD"),
        // 쌍검
        new(new TraitDef("SKL_FLURRY", "난무(스킬)"), "", 1,
            "허점에 칼비가 쏟아진다 — 경직·스태거 상대에 5연타(타당 약공 ×0.5) (ST28 / 10s · 主ATK)",
            new ActiveSpec("FLURRY", SkillTrigger.OppVulnerable, 0f, 0.55f, 0f, 10f, ActiveKind.Strike,
                StCost: 28f, StrikeDmgMult: 0.5f, StrikeHits: 5), GateWeapon: "WPN_DUALBLADES"),
        new(new TraitDef("SKL_MIRAGE", "그림자 보(스킬)"), "", 2,
            "그림자만 남기고 사라진다 — 상대 배후로 이동 + 다음 약공 확정 크리 (ST20 / 20s · 主SPD)",
            new ActiveSpec("MIRAGE", SkillTrigger.OppHeavyWindupOrRecovery, 0f, 0.6f, 0f, 20f, ActiveKind.Strike,
                StCost: 20f, TeleportBehind: true, NextLightCritSec: 3f), GateWeapon: "WPN_DUALBLADES"),
        // 망치
        new(new TraitDef("SKL_SMASH", "대지 강타(스킬)"), "", 1,
            "땅째 부수는 일격 — 강공 ×1.3 + 가드관통 50% + 명중 시 스태거 (ST22 / 11s · 主ATK)",
            new ActiveSpec("SMASH", SkillTrigger.InRange, 0f, 0.5f, 0f, 11f, ActiveKind.Strike,
                StCost: 22f, StrikeHeavy: true, StrikeDmgMult: 1.3f, GuardPierce: 0.5f, StaggerOnHitSec: 0.8f), GateWeapon: "WPN_HAMMER"),
        new(new TraitDef("SKL_EXECUTE", "심판의 일격(스킬)"), "", 2,
            "빈사의 상대에게 심판이 내린다 — 1.2초 무방비 차지 후 강공 ×2.5, HP 15% 미만이면 즉사 (CD만 / 28s · 主ATK, 고결은 거부)",
            new ActiveSpec("EXECUTE", SkillTrigger.OppExecutable, 0.35f, 0.8f, 1.2f, 28f, ActiveKind.Charge,
                ChargeSec: 1.2f, ExecuteDmgMult: 2.5f, ExecuteKillPct: 0.15f, VetoExecution: true), GateWeapon: "WPN_HAMMER"),
        // 채찍 — 카이터 복원 핵심([7])
        new(new TraitDef("SKL_ENTANGLE", "휘감기(스킬)"), "", 1,
            "가죽이 발목을 삼킨다 — 피해 + 멀면 끌어당김·가까우면 1초 이동봉쇄 (ST20 / 12s · 主SPD)",
            new ActiveSpec("ENTANGLE", SkillTrigger.GapBand, 0f, 0.6f, 0f, 12f, ActiveKind.Strike,
                StCost: 20f, GapMinM: 3.0f, GapMaxM: 4.5f, PullM: 1.2f, RootSec: 1.0f), GateWeapon: "WPN_WHIP"),
        new(new TraitDef("SKL_ZONELOCK", "공간 지배(스킬)"), "", 2,
            "이 원 안은 내 것이다 — 6초간 사거리 진입자 자동 견제(약공 ×0.6/0.8s) + 카이팅 소모 면제 (CD만 / 26s · 主DEF)",
            new ActiveSpec("ZONELOCK", SkillTrigger.GapBand, 0f, 0.6f, 6f, 26f,
                GapMinM: 0f, GapMaxM: 2.25f, KiteExempt: true, AutoPokeMult: 0.6f, AutoPokeIntervalSec: 0.8f), GateWeapon: "WPN_WHIP"),
        // 방패
        new(new TraitDef("SKL_SHIELDBLOCK", "방패 막기(스킬)"), "", 1,   // id는 패시브 '불괴(SKL_BULWARK)'와 충돌 회피 — reasonTag는 [7]대로 BULWARK
            "정면은 뚫리지 않는다 — 0.8초 완전 차단 + 직후 1초 반격 +30% (GG20 / 8s · 主DEF, 반응형 최우선)",
            new ActiveSpec("BULWARK", SkillTrigger.OppWindupAny, 0f, 0.7f, 0.8f, 8f, ActiveKind.Stance,
                GgCost: 20f, FullBlock: true, CounterBoostMult: 1.3f, CounterBoostSec: 1f), GateWeapon: "WPN_SHIELD"),
        new(new TraitDef("SKL_SHIELDBASH", "방패 밀치기(스킬)"), "", 2,
            "방패가 무기가 되는 순간 — 돌진 방패치기: 가드붕괴 + 다운(면역이면 붕괴만) (ST25 / 20s · 主DEF)",
            new ActiveSpec("SHIELDBASH", SkillTrigger.OppGuardingOrStunned, 0f, 0.6f, 0f, 20f, ActiveKind.Strike,
                StCost: 25f, DashIn: true, BashBreak: true, DownSec: 1.5f), GateWeapon: "WPN_SHIELD"),
    };

    private static readonly Dictionary<string, SkillDef> _byId = All.ToDictionary(s => s.Def.Id);
    public static SkillDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}
