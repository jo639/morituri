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
    SelfHpBelow,      // 자기 HP 비율 ≤ 임계 (광전사의 도끼)
    EvenFight,        // 호각 — HP 격차 ≤ 임계%p & 교전 지속 (결투의 격)
    ConsecHitsTaken,  // 연속 피격 ≥ 임계 (불퇴의 자세)
    OppGuarding,      // 상대 가드 중 & 사거리내 (분쇄 일격)
}

/// <summary>
/// [7] 무기 액티브 명세 — <b>AI가 조건·확률로 발동</b>한다(관전형 — 감독이 누르는 게 아님, [7] 전제).
/// 발동 = [7]§1 트리(쿨→상태→코스트→조건→타당성→확률 롤) 통과. 효과 = 시한 버프·1회 플래그.
/// 코스트: ST(공격 버스트)/HP%(배수진)/CD만(수비·유틸) — [7]§0.
/// </summary>
public sealed record ActiveSpec(
    string ReasonTag,                       // [7] 가시화 원칙 — Decision("SKILL_"+tag)로 발동 방출
    SkillTrigger Trigger, float Threshold, float Prob,
    float Duration, float CooldownSec,
    float StCost = 0f, float SelfHpPctCost = 0f,
    float CounterWindowAdd = 0f,            // 결투의 격: 카운터창 +0.3 (Override 파이프, 캡 +0.6 [7]§2)
    float DmgTakenMult = 1f,                // 광전사의 도끼: 받피 +25% (설계 의도된 리스크)
    float AtkPerMissingHpPct = 0f, float AtkCap = 0f,   // 광전사: 공격력 +0.8%/(부족 HP%p), 최대 +40%
    bool PoiseImmune = false,               // 불퇴의 자세: 포이즈 무한 = 스태거/넉백 면역(가드붕괴·다운은 아님)
    bool SunderNextHeavy = false);          // 분쇄 일격: 다음 강공 가드 무조건파괴 1회(미사용 시 만료 소멸)

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

        // ── 무기 액티브([7]§4 카탈로그) — 1차 탑재: 모션이 필요 없는 버프·자세·플래그형 4종.
        //    코스트/CD/확률/트리 = 문서 수치 그대로. 나머지(연격·견제 찌르기·철벽 반격·쇄도 베기·난무·
        //    그림자 보·대지 강타·심판의 일격·휘감기·공간 지배·방패 막기·방패 밀치기)는 전용 모션·CC·
        //    자동공격 경로가 필요해 애니메이션 트랙에서 탑재한다.
        new(new TraitDef("SKL_DUELIST", "결투의 격(스킬)"), "", 2,
            "호각의 상대와 격이 오른다 — 6초간 카운터 창 +0.3 (CD만 / 24s · 主RCT)",
            new ActiveSpec("DUELIST", SkillTrigger.EvenFight, 0.10f, 0.6f, 6f, 24f,
                CounterWindowAdd: 0.3f), GateWeapon: "WPN_SWORD"),
        new(new TraitDef("SKL_SUNDER", "분쇄 일격(스킬)"), "", 1,
            "가드째 부순다 — 다음 강공이 가드를 무조건 파괴 + 출혈 (ST22 / 11s · 主ATK, 5초 내 미사용 시 소멸)",
            new ActiveSpec("SUNDER", SkillTrigger.OppGuarding, 0f, 0.6f, 5f, 11f,
                StCost: 22f, SunderNextHeavy: true), GateWeapon: "WPN_AXE"),
        new(new TraitDef("SKL_BERSERK", "광전사의 도끼(스킬)"), "", 2,
            "제 피를 값으로 치른다 — HP 5% 자해, 8초간 공격력 +0.8%/(부족 HP%p) 최대 +40%·받는 피해 +25% (26s · 主ATK)",
            new ActiveSpec("BERSERK", SkillTrigger.SelfHpBelow, 0.50f, 0.7f, 8f, 26f,
                SelfHpPctCost: 0.05f, DmgTakenMult: 1.25f, AtkPerMissingHpPct: 0.008f, AtkCap: 0.40f), GateWeapon: "WPN_AXE"),
        new(new TraitDef("SKL_UNBROKEN", "불퇴의 자세(스킬)"), "", 2,
            "물러서지 않는다 — 5초간 포이즈 무한(스태거·넉백 면역, 가드붕괴·다운은 아님) (CD만 / 24s · 主DEF)",
            new ActiveSpec("UNBROKEN", SkillTrigger.ConsecHitsTaken, 2f, 0.6f, 5f, 24f,
                PoiseImmune: true), GateWeapon: "WPN_GREATSWORD"),
    };

    private static readonly Dictionary<string, SkillDef> _byId = All.ToDictionary(s => s.Def.Id);
    public static SkillDef Get(string id) => _byId[id];
    public static bool Exists(string id) => _byId.ContainsKey(id);
}
